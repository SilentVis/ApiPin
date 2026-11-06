using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiPin.Core.Execution;
using ApiPin.Core.Http;
using ApiPin.Core.Models;

namespace ApiPin.Storage.Postman;

/// <summary>
/// Writes a collection as Postman Collection Format v2.1, which Postman, Insomnia, Bruno and others can import.
/// Settings Postman has no equivalent for (per-request timeout, HTTP version) are left out.
/// Collection and folder auth carry over (requests set to inherit leave <c>auth</c> out, as Postman does);
/// collection and folder headers, which Postman lacks, are added to each request inside.
/// </summary>
public static class PostmanExporter
{
    public const string SchemaUrl = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";

    private static readonly JsonSerializerOptions Output = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Export(RequestCollection collection)
    {
        var info = new JsonObject
        {
            ["_postman_id"] = collection.Id.ToString(),
            ["name"] = collection.Name,
            ["schema"] = SchemaUrl,
        };
        if (!string.IsNullOrWhiteSpace(collection.Description)) info["description"] = collection.Description;

        var root = new JsonObject { ["info"] = info, ["item"] = Items(collection, []) };
        if (FolderAuth(collection) is { } auth) root["auth"] = auth;

        var variables = collection.Variables.Concat(collection.SelfAndDescendants().Skip(1).SelectMany(f => f.Variables))
            .Where(v => v.Key.Trim().Length > 0)
            .GroupBy(v => v.Key.Trim())
            .Select(g => g.First());
        var variableArray = new JsonArray(variables.Select(v =>
        {
            var obj = new JsonObject { ["key"] = v.Key.Trim(), ["value"] = v.Secret ? "" : v.Value, ["type"] = v.Secret ? "secret" : "string" };
            if (!v.Enabled) obj["disabled"] = true;
            return (JsonNode)obj;
        }).ToArray());
        if (variableArray.Count > 0) root["variable"] = variableArray;

        return root.ToJsonString(Output);
    }

    /// <summary>Suggested file name, following Postman's own convention.</summary>
    public static string FileName(RequestCollection collection) => $"{Slug.From(collection.Name, "collection")}.postman_collection.json";

    /// <param name="chain">Folders above <paramref name="folder"/>, outermost first, for inherited headers.</param>
    private static JsonArray Items(RequestFolder folder, List<RequestFolder> chain)
    {
        List<RequestFolder> path = [.. chain, folder];
        var items = new JsonArray();
        foreach (var sub in folder.OrderedFolders)
        {
            var item = new JsonObject { ["name"] = sub.Name, ["item"] = Items(sub, path) };
            if (!string.IsNullOrWhiteSpace(sub.Description)) item["description"] = sub.Description;
            if (FolderAuth(sub) is { } auth) item["auth"] = auth;
            items.Add(item);
        }
        foreach (var request in folder.OrderedRequests) items.Add(Item(request, path));
        return items;
    }

    private static JsonObject? FolderAuth(RequestFolder folder) =>
        folder.Auth is InheritAuth ? null : Auth(folder.Auth, new JsonArray());

    private static JsonObject Item(RequestDefinition r, IReadOnlyList<RequestFolder> chain)
    {
        var inherited = InheritanceResolver.InheritedHeaders(r.Headers, chain).Where(h => !h.Overridden).Select(h => h.Header);
        var headers = Headers(inherited.Concat(r.Headers));
        var request = new JsonObject
        {
            ["method"] = r.Method.Trim() is { Length: > 0 } m ? m.ToUpperInvariant() : "GET",
            ["header"] = headers,
        };

        if (Auth(r.Auth, headers) is { } auth) request["auth"] = auth;
        if (Body(r.Body, headers) is { } body) request["body"] = body;
        request["url"] = Url(r);

        var item = new JsonObject
        {
            ["name"] = string.IsNullOrWhiteSpace(r.Name) ? r.Url : r.Name,
            ["request"] = request,
            ["response"] = new JsonArray(),
        };
        if (ProtocolProfile(r.Settings) is { } behavior) item["protocolProfileBehavior"] = behavior;
        return item;
    }

    private static JsonArray Headers(IEnumerable<KeyValueItem> headers)
    {
        var array = new JsonArray();
        foreach (var h in headers.Where(h => h.Key.Trim().Length > 0))
            array.Add(Pair(h, "text"));
        return array;
    }

    private static JsonObject? Auth(AuthConfig auth, JsonArray headers)
    {
        switch (auth)
        {
            case InheritAuth:
                return null; // Postman: no auth object means inherit from the parent

            case BasicAuth basic:
                return Typed("basic", ("username", basic.Username), ("password", basic.Password));

            case BearerAuth bearer when bearer.Prefix.Trim().Equals("Bearer", StringComparison.OrdinalIgnoreCase):
                return Typed("bearer", ("token", bearer.Token.Trim()));

            case BearerAuth bearer:
                // Postman's bearer type always writes "Bearer"; keep a custom prefix as a plain header.
                var value = bearer.Prefix.Trim().Length == 0 ? bearer.Token.Trim() : $"{bearer.Prefix.Trim()} {bearer.Token.Trim()}";
                headers.Add(new JsonObject { ["key"] = "Authorization", ["value"] = value, ["type"] = "text" });
                return Typed("noauth");

            case ApiKeyAuth apiKey:
                return Typed("apikey",
                    ("key", apiKey.Key),
                    ("value", apiKey.Value),
                    ("in", apiKey.Location == ApiKeyLocation.Query ? "query" : "header"));

            case OAuth2Auth o:
                var grant = o.GrantType switch
                {
                    OAuth2GrantType.AuthorizationCode => o.UsePkce ? "authorization_code_with_pkce" : "authorization_code",
                    OAuth2GrantType.Password => "password_credentials",
                    _ => "client_credentials",
                };
                var values = new List<(string, string)>
                {
                    ("grant_type", grant), ("accessTokenUrl", o.TokenUrl), ("clientId", o.ClientId), ("clientSecret", o.ClientSecret),
                    ("scope", o.Scope), ("client_authentication", o.ClientAuthentication == OAuth2ClientAuthentication.Body ? "body" : "header"),
                    ("addTokenTo", "header"), ("headerPrefix", o.HeaderPrefix),
                };
                if (o.Audience.Length > 0) values.Add(("audience", o.Audience));
                if (o.GrantType == OAuth2GrantType.AuthorizationCode)
                {
                    values.AddRange([("authUrl", o.AuthorizationUrl), ("redirect_uri", o.RedirectUri)]);
                    if (o.UsePkce) values.Add(("challengeAlgorithm", "S256"));
                }
                if (o.GrantType == OAuth2GrantType.Password) values.AddRange([("username", o.Username), ("password", o.Password)]);
                return Typed("oauth2", [.. values]);

            case AzureCliAuth:
                return Typed("noauth"); // Postman has no Azure CLI auth; set a bearer token there instead

            case WindowsAuth windows:
                // Postman needs explicit credentials for NTLM; "current user" exports with them left blank.
                return windows.UseCurrentUser
                    ? Typed("ntlm", ("username", ""), ("password", ""), ("domain", ""))
                    : Typed("ntlm", ("username", windows.Username), ("password", windows.Password), ("domain", windows.Domain));

            default:
                return Typed("noauth");
        }
    }

    private static JsonObject Typed(string type, params (string Key, string Value)[] values)
    {
        var auth = new JsonObject { ["type"] = type };
        if (values.Length > 0)
        {
            auth[type] = new JsonArray(values
                .Select(v => (JsonNode)new JsonObject { ["key"] = v.Key, ["value"] = v.Value, ["type"] = "string" })
                .ToArray());
        }
        return auth;
    }

    private static JsonObject? Body(BodyConfig body, JsonArray headers) => body switch
    {
        RawBody raw => new JsonObject
        {
            ["mode"] = "raw",
            ["raw"] = raw.Content,
            ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = Language(raw.Language) } },
        },

        FormUrlEncodedBody form => new JsonObject
        {
            ["mode"] = "urlencoded",
            ["urlencoded"] = new JsonArray(form.Fields.Where(f => f.Key.Length > 0).Select(f => (JsonNode)Pair(f, "text")).ToArray()),
        },

        MultipartBody multipart => new JsonObject
        {
            ["mode"] = "formdata",
            ["formdata"] = new JsonArray(multipart.Parts.Where(p => p.Name.Length > 0).Select(p => (JsonNode)Part(p)).ToArray()),
        },

        BinaryFileBody binary => BinaryBody(binary, headers),

        _ => null,
    };

    private static JsonObject BinaryBody(BinaryFileBody binary, JsonArray headers)
    {
        if (!string.IsNullOrWhiteSpace(binary.ContentType) && !headers.Any(h => h?["key"]?.GetValue<string>().Equals("Content-Type", StringComparison.OrdinalIgnoreCase) == true))
            headers.Add(new JsonObject { ["key"] = "Content-Type", ["value"] = binary.ContentType, ["type"] = "text" });

        return new JsonObject { ["mode"] = "file", ["file"] = new JsonObject { ["src"] = binary.FilePath } };
    }

    private static JsonObject Part(MultipartPart part)
    {
        var obj = new JsonObject { ["key"] = part.Name };
        if (part.Kind == MultipartPartKind.File)
        {
            obj["type"] = "file";
            obj["src"] = part.Value;
        }
        else
        {
            obj["value"] = part.Value;
            obj["type"] = "text";
        }
        if (!string.IsNullOrWhiteSpace(part.ContentType)) obj["contentType"] = part.ContentType;
        if (!part.Enabled) obj["disabled"] = true;
        return obj;
    }

    private static string Language(RawLanguage language) => language switch
    {
        RawLanguage.Json => "json",
        RawLanguage.Xml => "xml",
        RawLanguage.Html => "html",
        RawLanguage.JavaScript => "javascript",
        _ => "text",
    };

    /// <summary>Postman's URL object: raw text plus its parsed parts, disabled query params and path variables.</summary>
    private static JsonObject Url(RequestDefinition r)
    {
        var raw = r.Url.Trim();
        var url = new JsonObject { ["raw"] = raw };

        var (beforeQuery, _, _) = UrlTools.Split(raw);
        var rest = beforeQuery;
        var scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            url["protocol"] = rest[..scheme];
            rest = rest[(scheme + 3)..];
        }

        var slash = rest.IndexOf('/');
        var authority = slash >= 0 ? rest[..slash] : rest;
        var path = slash >= 0 ? rest[(slash + 1)..] : "";

        var colon = authority.LastIndexOf(':');
        if (colon > 0 && !authority.EndsWith(']') && authority[(colon + 1)..].All(char.IsAsciiDigit) && colon < authority.Length - 1)
        {
            url["port"] = authority[(colon + 1)..];
            authority = authority[..colon];
        }
        if (authority.Length > 0)
            url["host"] = new JsonArray(authority.Split('.').Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
        if (slash >= 0)
            url["path"] = new JsonArray(path.Split('/').Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());

        var query = r.QueryParams.Where(q => q.Key.Length > 0 || q.Value.Length > 0).ToList();
        if (query.Count > 0)
            url["query"] = new JsonArray(query.Select(q => (JsonNode)Pair(q, type: null)).ToArray());

        if (r.PathParams.Count > 0)
        {
            url["variable"] = new JsonArray(r.PathParams.Select(p =>
            {
                var v = new JsonObject { ["key"] = p.Key, ["value"] = p.Value };
                if (!string.IsNullOrWhiteSpace(p.Description)) v["description"] = p.Description;
                return (JsonNode)v;
            }).ToArray());
        }

        return url;
    }

    private static JsonObject? ProtocolProfile(RequestSettings s)
    {
        var behavior = new JsonObject();
        if (!s.FollowRedirects) behavior["followRedirects"] = false;
        else if (s.MaxRedirects != new RequestSettings().MaxRedirects) behavior["maxRedirects"] = s.MaxRedirects;
        if (!s.VerifyTls) behavior["strictSSL"] = false;
        return behavior.Count > 0 ? behavior : null;
    }

    private static JsonObject Pair(KeyValueItem item, string? type)
    {
        var obj = new JsonObject { ["key"] = item.Key.Trim(), ["value"] = item.Value };
        if (type is not null) obj["type"] = type;
        if (!item.Enabled) obj["disabled"] = true;
        if (!string.IsNullOrWhiteSpace(item.Description)) obj["description"] = item.Description;
        return obj;
    }
}
