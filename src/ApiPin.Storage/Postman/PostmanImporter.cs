using System.Text.Json;
using ApiPin.Core.Http;
using ApiPin.Core.Models;

namespace ApiPin.Storage.Postman;

/// <summary>What an import produced, plus anything that couldn't be carried over.</summary>
public sealed record ImportResult(RequestCollection? Collection, EnvironmentDefinition? Environment, IReadOnlyList<string> Warnings);

public sealed class ImportException(string message) : Exception(message);

/// <summary>
/// Reads Postman Collection v2.0/v2.1 files and Postman environment files. Scripts (pre-request and tests)
/// have no equivalent and are reported as warnings.
/// </summary>
public static class PostmanImporter
{
    public static bool IsCollection(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("info", out var info) && info.TryGetProperty("schema", out var schema) &&
        schema.GetString()?.Contains("getpostman.com", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsEnvironment(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array &&
        root.TryGetProperty("name", out _);

    /// <exception cref="ImportException">Not a Postman collection or environment.</exception>
    public static ImportResult Import(string json)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }).RootElement;
        }
        catch (JsonException ex)
        {
            throw new ImportException($"Not valid JSON: {ex.Message}");
        }

        var warnings = new List<string>();
        if (IsCollection(root)) return new ImportResult(Collection(root, warnings), null, warnings);
        if (IsEnvironment(root)) return new ImportResult(null, Environment(root), warnings);
        throw new ImportException("This isn't a Postman collection (v2.0/v2.1) or environment file.");
    }

    private static RequestCollection Collection(JsonElement root, List<string> warnings)
    {
        var info = root.GetProperty("info");
        var collection = new RequestCollection
        {
            Name = Str(info, "name") ?? "Imported collection",
            Description = Description(info),
            Auth = root.TryGetProperty("auth", out var auth) ? Auth(auth, warnings, "the collection") : new NoAuth(),
        };
        if (root.TryGetProperty("variable", out var vars) && vars.ValueKind == JsonValueKind.Array)
        {
            collection.Variables = vars.EnumerateArray().Select(v => new Variable
            {
                Key = Str(v, "key") ?? "",
                Value = ValueText(v),
                Enabled = !Bool(v, "disabled"),
                Secret = Str(v, "type") == "secret",
            }).Where(v => v.Key.Length > 0).ToList();
        }
        Scripts(root, "the collection", warnings);
        if (root.TryGetProperty("item", out var items)) Items(collection, items, warnings);
        return collection;
    }

    private static void Items(RequestFolder parent, JsonElement items, List<string> warnings)
    {
        if (items.ValueKind != JsonValueKind.Array) return;
        var seq = 0;
        foreach (var item in items.EnumerateArray())
        {
            var name = Str(item, "name") ?? "Untitled";
            if (item.TryGetProperty("item", out var children))
            {
                var folder = new RequestFolder
                {
                    Name = name,
                    Seq = seq++,
                    Description = Description(item),
                    Auth = item.TryGetProperty("auth", out var auth) ? Auth(auth, warnings, $"folder '{name}'") : new InheritAuth(),
                };
                Scripts(item, $"folder '{name}'", warnings);
                Items(folder, children, warnings);
                parent.Folders.Add(folder);
            }
            else if (item.TryGetProperty("request", out var request))
            {
                Scripts(item, $"request '{name}'", warnings);
                var r = Request(request, name, warnings);
                r.Seq = seq++;
                if (item.TryGetProperty("protocolProfileBehavior", out var behavior))
                {
                    if (behavior.TryGetProperty("followRedirects", out var follow) && follow.ValueKind == JsonValueKind.False) r.Settings.FollowRedirects = false;
                    if (behavior.TryGetProperty("strictSSL", out var strict) && strict.ValueKind == JsonValueKind.False) r.Settings.VerifyTls = false;
                    if (behavior.TryGetProperty("maxRedirects", out var max) && max.TryGetInt32(out var m)) r.Settings.MaxRedirects = m;
                }
                parent.Requests.Add(r);
            }
        }
    }

    private static RequestDefinition Request(JsonElement request, string name, List<string> warnings)
    {
        var r = new RequestDefinition { Name = name };

        if (request.ValueKind == JsonValueKind.String)
        {
            r.Url = request.GetString() ?? "";
        }
        else
        {
            r.Method = (Str(request, "method") ?? "GET").ToUpperInvariant();
            if (request.TryGetProperty("url", out var url)) ReadUrl(r, url);
            if (request.TryGetProperty("header", out var headers) && headers.ValueKind == JsonValueKind.Array)
                r.Headers = headers.EnumerateArray().Select(Pair).Where(h => h.Key.Length > 0).ToList();
            r.Auth = request.TryGetProperty("auth", out var auth) && auth.ValueKind == JsonValueKind.Object ? Auth(auth, warnings, $"request '{name}'") : new InheritAuth();
            if (request.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object) r.Body = Body(body, r, warnings, name);
        }

        if (r.QueryParams.Count == 0) r.QueryParams = UrlTools.SyncQueryParams(r.Url, []);
        r.PathParams = UrlTools.SyncPathParams(r.Url, r.PathParams);
        return r;
    }

    private static void ReadUrl(RequestDefinition r, JsonElement url)
    {
        if (url.ValueKind == JsonValueKind.String)
        {
            r.Url = url.GetString() ?? "";
            return;
        }
        r.Url = Str(url, "raw") ?? Rebuild(url);

        var enabled = new List<KeyValueItem>();
        var disabled = new List<KeyValueItem>();
        if (url.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in query.EnumerateArray())
            {
                var item = Pair(q);
                (item.Enabled ? enabled : disabled).Add(item);
            }
            // The raw URL already has the enabled ones; keep the disabled ones as rows.
            r.QueryParams = [.. UrlTools.SyncQueryParams(r.Url, []), .. disabled];
        }
        if (url.TryGetProperty("variable", out var vars) && vars.ValueKind == JsonValueKind.Array)
            r.PathParams = vars.EnumerateArray().Select(v => new KeyValueItem { Key = Str(v, "key") ?? "", Value = ValueText(v), Description = DescriptionText(v) }).ToList();
    }

    private static string Rebuild(JsonElement url)
    {
        var protocol = Str(url, "protocol");
        var host = url.TryGetProperty("host", out var h) ? h.ValueKind == JsonValueKind.Array ? string.Join(".", h.EnumerateArray().Select(x => x.GetString())) : h.GetString() : "";
        var port = Str(url, "port");
        var path = url.TryGetProperty("path", out var p) ? p.ValueKind == JsonValueKind.Array ? string.Join("/", p.EnumerateArray().Select(x => x.GetString())) : p.GetString() : "";
        return $"{(protocol is null ? "" : protocol + "://")}{host}{(port is null ? "" : ":" + port)}/{path?.TrimStart('/')}";
    }

    private static BodyConfig Body(JsonElement body, RequestDefinition r, List<string> warnings, string name)
    {
        if (Bool(body, "disabled")) return new NoBody();
        switch (Str(body, "mode"))
        {
            case "raw":
                var language = body.TryGetProperty("options", out var o) && o.TryGetProperty("raw", out var raw) ? Str(raw, "language") : null;
                var content = Str(body, "raw") ?? "";
                var ct = r.Headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
                return new RawBody
                {
                    Content = content,
                    Language = (language ?? "") switch
                    {
                        "json" => RawLanguage.Json,
                        "xml" => RawLanguage.Xml,
                        "html" => RawLanguage.Html,
                        "javascript" => RawLanguage.JavaScript,
                        _ when ct.Contains("json", StringComparison.OrdinalIgnoreCase) => RawLanguage.Json,
                        _ when ct.Contains("xml", StringComparison.OrdinalIgnoreCase) => RawLanguage.Xml,
                        _ => RawLanguage.Text,
                    },
                };

            case "urlencoded":
                return new FormUrlEncodedBody { Fields = Array(body, "urlencoded").Select(Pair).ToList() };

            case "formdata":
                return new MultipartBody
                {
                    Parts = Array(body, "formdata").Select(p => Str(p, "type") == "file"
                        ? new MultipartPart { Name = Str(p, "key") ?? "", Kind = MultipartPartKind.File, Value = SrcText(p), ContentType = Str(p, "contentType"), Enabled = !Bool(p, "disabled") }
                        : new MultipartPart { Name = Str(p, "key") ?? "", Value = Str(p, "value") ?? "", ContentType = Str(p, "contentType"), Enabled = !Bool(p, "disabled") }).ToList(),
                };

            case "file":
                return new BinaryFileBody { FilePath = body.TryGetProperty("file", out var f) ? SrcText(f) : "" };

            case "graphql":
                var gql = body.TryGetProperty("graphql", out var g) ? g : default;
                return new GraphQlBody { Query = gql.ValueKind == JsonValueKind.Object ? Str(gql, "query") ?? "" : "", Variables = gql.ValueKind == JsonValueKind.Object ? Str(gql, "variables") ?? "" : "" };

            case null:
                return new NoBody();

            default:
                warnings.Add($"Request '{name}': body mode '{Str(body, "mode")}' isn't supported and was left out.");
                return new NoBody();
        }
    }

    private static AuthConfig Auth(JsonElement auth, List<string> warnings, string where)
    {
        var type = Str(auth, "type");
        Dictionary<string, string> V(string key) => auth.TryGetProperty(key, out var values)
            ? values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().GroupBy(e => Str(e, "key") ?? "").ToDictionary(g => g.Key, g => ValueText(g.First()))
                : values.ValueKind == JsonValueKind.Object // v2.0: { "username": "…" }
                    ? values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString())
                    : []
            : [];

        switch (type)
        {
            case "noauth": return new NoAuth();
            case "inherit": return new InheritAuth();
            case "basic":
                var b = V("basic");
                return new BasicAuth { Username = b.GetValueOrDefault("username", ""), Password = b.GetValueOrDefault("password", "") };
            case "bearer":
                return new BearerAuth { Token = V("bearer").GetValueOrDefault("token", "") };
            case "apikey":
                var k = V("apikey");
                return new ApiKeyAuth { Key = k.GetValueOrDefault("key", "X-API-Key"), Value = k.GetValueOrDefault("value", ""), Location = k.GetValueOrDefault("in") == "query" ? ApiKeyLocation.Query : ApiKeyLocation.Header };
            case "ntlm":
                var n = V("ntlm");
                var user = n.GetValueOrDefault("username", "");
                return new WindowsAuth { UseCurrentUser = user.Length == 0, Username = user, Password = n.GetValueOrDefault("password", ""), Domain = n.GetValueOrDefault("domain", "") };
            case "oauth2":
                var o = V("oauth2");
                var grant = o.GetValueOrDefault("grant_type", "client_credentials");
                return new OAuth2Auth
                {
                    GrantType = grant switch
                    {
                        "authorization_code" or "authorization_code_with_pkce" => OAuth2GrantType.AuthorizationCode,
                        "password_credentials" => OAuth2GrantType.Password,
                        _ => OAuth2GrantType.ClientCredentials,
                    },
                    UsePkce = grant == "authorization_code_with_pkce" || o.ContainsKey("challengeAlgorithm"),
                    TokenUrl = o.GetValueOrDefault("accessTokenUrl", ""),
                    AuthorizationUrl = o.GetValueOrDefault("authUrl", ""),
                    ClientId = o.GetValueOrDefault("clientId", ""),
                    ClientSecret = o.GetValueOrDefault("clientSecret", ""),
                    Scope = o.GetValueOrDefault("scope", ""),
                    Audience = o.GetValueOrDefault("audience", ""),
                    Username = o.GetValueOrDefault("username", ""),
                    Password = o.GetValueOrDefault("password", ""),
                    RedirectUri = o.GetValueOrDefault("redirect_uri", OAuth2Auth.DefaultRedirectUri),
                    ClientAuthentication = o.GetValueOrDefault("client_authentication") == "body" ? OAuth2ClientAuthentication.Body : OAuth2ClientAuthentication.BasicHeader,
                    HeaderPrefix = o.GetValueOrDefault("headerPrefix", "Bearer"),
                };
            default:
                warnings.Add($"Auth type '{type}' on {where} isn't supported; it was set to no auth.");
                return new NoAuth();
        }
    }

    private static EnvironmentDefinition Environment(JsonElement root) => new()
    {
        Name = Str(root, "name") ?? "Imported environment",
        Variables = root.GetProperty("values").EnumerateArray().Select(v => new Variable
        {
            Key = Str(v, "key") ?? "",
            Value = ValueText(v),
            Enabled = !v.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False,
            Secret = Str(v, "type") == "secret",
        }).Where(v => v.Key.Length > 0).ToList(),
    };

    private static void Scripts(JsonElement item, string where, List<string> warnings)
    {
        if (!item.TryGetProperty("event", out var events) || events.ValueKind != JsonValueKind.Array) return;
        foreach (var e in events.EnumerateArray())
        {
            var hasCode = e.TryGetProperty("script", out var s) && s.TryGetProperty("exec", out var exec) &&
                          (exec.ValueKind == JsonValueKind.Array ? exec.EnumerateArray().Any(l => l.GetString()?.Trim().Length > 0) : exec.GetString()?.Trim().Length > 0);
            if (hasCode) warnings.Add($"The {Str(e, "listen")} script on {where} was not imported (ApiPin has no scripts; use captures and assertions).");
        }
    }

    private static KeyValueItem Pair(JsonElement e) => new()
    {
        Key = Str(e, "key") ?? "",
        Value = ValueText(e),
        Enabled = !Bool(e, "disabled"),
        Description = DescriptionText(e),
    };

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string ValueText(JsonElement e) =>
        e.TryGetProperty("value", out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : v.ToString() : "";

    private static string SrcText(JsonElement e) =>
        e.TryGetProperty("src", out var src) ? src.ValueKind == JsonValueKind.Array ? src.EnumerateArray().FirstOrDefault().GetString() ?? "" : src.GetString() ?? "" : "";

    private static string? Description(JsonElement e) => DescriptionText(e) is { Length: > 0 } d ? d : null;

    private static string? DescriptionText(JsonElement e) =>
        e.TryGetProperty("description", out var d) ? d.ValueKind == JsonValueKind.String ? d.GetString() : d.ValueKind == JsonValueKind.Object ? Str(d, "content") : null : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
