using System.Text.Json.Nodes;
using ApiPin.Core.Http;
using ApiPin.Core.Models;
using ApiPin.Storage.Postman;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Microsoft.OpenApi.Writers;

namespace ApiPin.Storage.OpenApi;

/// <summary>
/// Turns an OpenAPI 3 (or Swagger 2.0) description, JSON or YAML, into a collection: one folder per tag, one request
/// per operation, <c>{{baseUrl}}</c> from the first server, example bodies from examples or generated from schemas,
/// and collection auth from the security schemes (values left as variables to fill in).
/// </summary>
public static class OpenApiImporter
{
    private const int MaxSampleDepth = 6;

    public static bool LooksLikeOpenApi(string text)
    {
        var head = text.Length > 4000 ? text[..4000] : text;
        return head.Contains("\"openapi\"", StringComparison.Ordinal) || head.Contains("\"swagger\"", StringComparison.Ordinal) ||
               head.TrimStart().StartsWith("openapi:", StringComparison.Ordinal) || head.TrimStart().StartsWith("swagger:", StringComparison.Ordinal) ||
               head.Contains("\nopenapi:", StringComparison.Ordinal) || head.Contains("\nswagger:", StringComparison.Ordinal);
    }

    /// <exception cref="ImportException">The text can't be read as OpenAPI.</exception>
    public static ImportResult Import(string text)
    {
        OpenApiDocument document;
        OpenApiDiagnostic diagnostic;
        try
        {
            document = new OpenApiStringReader().Read(text, out diagnostic);
        }
        catch (Exception ex) when (ex is Microsoft.OpenApi.Exceptions.OpenApiException or FormatException or InvalidOperationException)
        {
            throw new ImportException($"Not a readable OpenAPI description: {ex.Message}");
        }
        if (document?.Paths is null || (document.Paths.Count == 0 && diagnostic.Errors.Count > 0))
            throw new ImportException("Not a readable OpenAPI description: " + string.Join("; ", diagnostic.Errors.Take(3).Select(e => e.Message)));

        var warnings = diagnostic.Errors.Take(10).Select(e => $"OpenAPI: {e.Message}").ToList();
        var collection = new RequestCollection
        {
            Name = string.IsNullOrWhiteSpace(document.Info?.Title) ? "Imported API" : document.Info.Title.Trim(),
            Description = document.Info?.Description,
            Variables = [new Variable { Key = "baseUrl", Value = ServerUrl(document) }],
        };
        ApplySecurity(document, collection, warnings);

        var folders = new Dictionary<string, RequestFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, item) in document.Paths)
        {
            foreach (var (type, operation) in item.Operations)
            {
                var request = Request(path, type, item, operation);
                var tag = operation.Tags?.FirstOrDefault()?.Name;
                RequestFolder parent = collection;
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    if (!folders.TryGetValue(tag, out var folder))
                    {
                        folder = new RequestFolder
                        {
                            Name = tag,
                            Seq = folders.Count,
                            Description = document.Tags?.FirstOrDefault(t => t.Name == tag)?.Description,
                        };
                        folders[tag] = folder;
                        collection.Folders.Add(folder);
                    }
                    parent = folder;
                }
                request.Seq = parent.Requests.Count;
                parent.Requests.Add(request);
            }
        }
        return new ImportResult(collection, null, warnings);
    }

    private static string ServerUrl(OpenApiDocument document)
    {
        if (document.Servers?.FirstOrDefault() is not { } server) return "http://localhost";
        var url = server.Url ?? "";
        foreach (var (name, variable) in server.Variables ?? new Dictionary<string, OpenApiServerVariable>())
            url = url.Replace("{" + name + "}", variable.Default);
        return url.TrimEnd('/');
    }

    private static RequestDefinition Request(string path, OperationType type, OpenApiPathItem item, OpenApiOperation operation)
    {
        var method = type.ToString().ToUpperInvariant();
        var request = new RequestDefinition
        {
            Name = !string.IsNullOrWhiteSpace(operation.Summary) ? operation.Summary.Trim()
                : !string.IsNullOrWhiteSpace(operation.OperationId) ? operation.OperationId
                : $"{method} {path}",
            Method = method,
        };

        // {id} → :id, the path-param syntax ApiPin edits.
        var url = "{{baseUrl}}" + System.Text.RegularExpressions.Regex.Replace(path, @"\{([^}/]+)\}", ":$1");
        var parameters = (item.Parameters ?? []).Concat(operation.Parameters ?? [])
            .GroupBy(p => (p.Name, p.In)).Select(g => g.Last()).ToList();

        var query = new List<KeyValueItem>();
        foreach (var p in parameters)
        {
            var example = Example(p.Example) ?? Example(p.Examples?.Values.FirstOrDefault()?.Value) ?? SampleText(p.Schema);
            switch (p.In)
            {
                case ParameterLocation.Query:
                    query.Add(new KeyValueItem { Key = p.Name, Value = example, Enabled = p.Required, Description = p.Description });
                    break;
                case ParameterLocation.Header:
                    request.Headers.Add(new KeyValueItem { Key = p.Name, Value = example, Enabled = p.Required, Description = p.Description });
                    break;
            }
        }
        request.Url = UrlTools.WithQuery(url, query);
        request.QueryParams = UrlTools.SyncQueryParams(request.Url, query.Where(q => !q.Enabled).ToList());
        // Keep descriptions on the enabled ones.
        foreach (var q in request.QueryParams) q.Description ??= query.FirstOrDefault(x => x.Key == q.Key)?.Description;
        request.PathParams = UrlTools.SyncPathParams(request.Url, []).Select(pp =>
        {
            var def = parameters.FirstOrDefault(p => p.In == ParameterLocation.Path && p.Name == pp.Key);
            pp.Description = def?.Description;
            pp.Value = def is null ? "" : Example(def.Example) ?? "";
            return pp;
        }).ToList();

        if (operation.RequestBody?.Content is { Count: > 0 } content) request.Body = Body(content);
        return request;
    }

    private static BodyConfig Body(IDictionary<string, OpenApiMediaType> content)
    {
        var (mediaType, media) = content.FirstOrDefault(c => c.Key.Contains("json", StringComparison.OrdinalIgnoreCase)) is { Value: not null } json
            ? json
            : content.First();

        if (mediaType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return new FormUrlEncodedBody
            {
                Fields = (media.Schema?.Properties ?? new Dictionary<string, OpenApiSchema>())
                    .Select(p => new KeyValueItem { Key = p.Key, Value = SampleText(p.Value), Description = p.Value.Description }).ToList(),
            };
        }
        if (mediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return new MultipartBody
            {
                Parts = (media.Schema?.Properties ?? new Dictionary<string, OpenApiSchema>()).Select(p => p.Value.Format == "binary"
                    ? new MultipartPart { Name = p.Key, Kind = MultipartPartKind.File }
                    : new MultipartPart { Name = p.Key, Value = SampleText(p.Value) }).ToList(),
            };
        }
        if (mediaType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase) || media.Schema?.Format == "binary")
            return new BinaryFileBody { ContentType = mediaType };

        var example = Example(media.Example) ?? Example(media.Examples?.Values.FirstOrDefault()?.Value);
        var isJson = mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);
        var text = example ?? (isJson && media.Schema is not null ? Sample(media.Schema, 0, [])?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) : null) ?? "";
        if (isJson && example is not null) text = ApiPin.Core.Formatting.BodyFormatter.TryPrettyJson(text) ?? text;
        return new RawBody
        {
            Language = isJson ? RawLanguage.Json : mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) ? RawLanguage.Xml : RawLanguage.Text,
            Content = text,
        };
    }

    private static void ApplySecurity(OpenApiDocument document, RequestCollection collection, List<string> warnings)
    {
        var schemes = document.Components?.SecuritySchemes;
        if (schemes is null || schemes.Count == 0) return;

        // The first scheme the document requires globally, else the first one defined.
        var name = document.SecurityRequirements?.SelectMany(r => r.Keys).FirstOrDefault()?.Reference?.Id ?? schemes.Keys.First();
        if (!schemes.TryGetValue(name, out var scheme)) return;

        void Var(string key, bool secret = false) => collection.Variables.Add(new Variable { Key = key, Secret = secret });

        switch (scheme.Type)
        {
            case SecuritySchemeType.Http when scheme.Scheme?.Equals("basic", StringComparison.OrdinalIgnoreCase) == true:
                collection.Auth = new BasicAuth { Username = "{{username}}", Password = "{{password}}" };
                Var("username");
                Var("password", secret: true);
                break;
            case SecuritySchemeType.Http:
                collection.Auth = new BearerAuth { Token = "{{token}}" };
                Var("token", secret: true);
                break;
            case SecuritySchemeType.ApiKey when scheme.In is ParameterLocation.Header or ParameterLocation.Query:
                collection.Auth = new ApiKeyAuth { Key = scheme.Name, Value = "{{apiKey}}", Location = scheme.In == ParameterLocation.Query ? ApiKeyLocation.Query : ApiKeyLocation.Header };
                Var("apiKey", secret: true);
                break;
            case SecuritySchemeType.OAuth2 when scheme.Flows is { } flows:
                var oauth = new OAuth2Auth { ClientId = "{{clientId}}", ClientSecret = "{{clientSecret}}" };
                if (flows.ClientCredentials is { } cc)
                {
                    oauth.TokenUrl = cc.TokenUrl?.ToString() ?? "";
                    oauth.Scope = string.Join(" ", cc.Scopes?.Keys ?? []);
                }
                else if (flows.AuthorizationCode is { } ac)
                {
                    oauth.GrantType = OAuth2GrantType.AuthorizationCode;
                    oauth.AuthorizationUrl = ac.AuthorizationUrl?.ToString() ?? "";
                    oauth.TokenUrl = ac.TokenUrl?.ToString() ?? "";
                    oauth.Scope = string.Join(" ", ac.Scopes?.Keys ?? []);
                }
                else if (flows.Password is { } pw)
                {
                    oauth.GrantType = OAuth2GrantType.Password;
                    oauth.TokenUrl = pw.TokenUrl?.ToString() ?? "";
                    oauth.Username = "{{username}}";
                    oauth.Password = "{{password}}";
                    Var("username");
                    Var("password", secret: true);
                }
                collection.Auth = oauth;
                Var("clientId");
                Var("clientSecret", secret: true);
                break;
            default:
                warnings.Add($"Security scheme '{name}' ({scheme.Type}) has no ApiPin equivalent; set the auth by hand.");
                break;
        }
    }

    private static string? Example(IOpenApiAny? any)
    {
        if (any is null) return null;
        if (any is IOpenApiPrimitive primitive)
        {
            return primitive switch
            {
                OpenApiString s => s.Value,
                OpenApiInteger i => i.Value.ToString(),
                OpenApiLong l => l.Value.ToString(),
                OpenApiDouble d => d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                OpenApiFloat f => f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                OpenApiBoolean b => b.Value ? "true" : "false",
                OpenApiDate date => date.Value.ToString("yyyy-MM-dd"),
                OpenApiDateTime dt => dt.Value.ToString("O"),
                _ => null,
            };
        }
        using var writer = new StringWriter();
        var json = new OpenApiJsonWriter(writer);
        any.Write(json, OpenApiSpecVersion.OpenApi3_0);
        json.Flush();
        return writer.ToString();
    }

    private static string SampleText(OpenApiSchema? schema) => Sample(schema, 0, []) switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        var node => node.ToJsonString(),
    };

    /// <summary>A plausible value for a schema: its example, default or first enum value, otherwise one made up by type.</summary>
    private static JsonNode? Sample(OpenApiSchema? schema, int depth, HashSet<string> seen)
    {
        if (schema is null || depth > MaxSampleDepth) return null;
        if (schema.Reference?.Id is { } refId && !seen.Add(refId)) return null; // recursive type
        try
        {
            if (Example(schema.Example) is { } example) return Parse(example, schema);
            if (Example(schema.Default) is { } defaultValue) return Parse(defaultValue, schema);
            if (schema.Enum?.FirstOrDefault() is { } first && Example(first) is { } enumValue) return Parse(enumValue, schema);

            if (schema.AllOf is { Count: > 0 })
            {
                var merged = new JsonObject();
                foreach (var part in schema.AllOf)
                    if (Sample(part, depth + 1, seen) is JsonObject obj)
                        foreach (var (k, v) in obj.ToList()) { obj.Remove(k); merged[k] = v; }
                return merged;
            }
            if ((schema.OneOf?.FirstOrDefault() ?? schema.AnyOf?.FirstOrDefault()) is { } alternative) return Sample(alternative, depth + 1, seen);

            switch (schema.Type)
            {
                case "array":
                    var array = new JsonArray();
                    if (Sample(schema.Items, depth + 1, seen) is { } item) array.Add(item);
                    return array;
                case "integer": return JsonValue.Create(0);
                case "number": return JsonValue.Create(0.0);
                case "boolean": return JsonValue.Create(true);
                case "string":
                    return JsonValue.Create(schema.Format switch
                    {
                        "date-time" => "2025-01-01T12:00:00Z",
                        "date" => "2025-01-01",
                        "uuid" => "00000000-0000-0000-0000-000000000000",
                        "email" => "user@example.com",
                        "uri" or "url" => "https://example.com",
                        _ => "string",
                    });
                default:
                    if (schema.Properties is { Count: > 0 } || schema.Type == "object")
                    {
                        var obj = new JsonObject();
                        foreach (var (name, property) in schema.Properties ?? new Dictionary<string, OpenApiSchema>())
                        {
                            if (property.ReadOnly) continue;
                            obj[name] = Sample(property, depth + 1, seen);
                        }
                        return obj;
                    }
                    return null;
            }
        }
        finally
        {
            if (schema.Reference?.Id is { } id) seen.Remove(id);
        }
    }

    private static JsonNode? Parse(string text, OpenApiSchema schema)
    {
        if (schema.Type is "string" or null && !(text.StartsWith('{') || text.StartsWith('['))) return JsonValue.Create(text);
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return JsonValue.Create(text); }
    }
}
