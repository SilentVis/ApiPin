using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ApiPin.Core.Models;

namespace ApiPin.Core.Http;

public sealed class RequestBuildException(string message) : Exception(message);

/// <summary>Turns a <see cref="RequestDefinition"/> into an <see cref="HttpRequestMessage"/>.</summary>
public static class HttpRequestBuilder
{
    public const string DefaultUserAgent = "ApiPin/0.1";

    /// <exception cref="RequestBuildException">The URL, method, a header or a body file is invalid.</exception>
    public static HttpRequestMessage Build(RequestDefinition request)
    {
        var uri = BuildUri(request);
        var message = new HttpRequestMessage(BuildMethod(request.Method), uri);
        try
        {
            ApplyVersion(message, request.Settings.HttpVersion);
            message.Content = BuildContent(request.Body);
            ApplyAuth(message, request.Auth);
            ApplyHeaders(message, request.Headers);

            if (!message.Headers.Contains("User-Agent")) message.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
            if (!message.Headers.Contains("Accept")) message.Headers.TryAddWithoutValidation("Accept", "*/*");
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    public static Uri BuildUri(RequestDefinition request)
    {
        var url = request.Url.Trim();
        if (url.Length == 0) throw new RequestBuildException("Enter a URL.");

        url = UrlTools.EnsureScheme(UrlTools.ApplyPathParams(url, request.PathParams));
        if (request.Auth is ApiKeyAuth { Location: ApiKeyLocation.Query } apiKey && apiKey.Key.Length > 0)
            url = UrlTools.AppendQueryParam(url, apiKey.Key, apiKey.Value);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new RequestBuildException($"'{url}' is not a valid URL.");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new RequestBuildException($"Unsupported scheme '{uri.Scheme}'. Use http or https.");
        return uri;
    }

    private static HttpMethod BuildMethod(string method)
    {
        method = method.Trim().ToUpperInvariant();
        if (method.Length == 0) return HttpMethod.Get;
        try
        {
            return HttpMethod.Parse(method);
        }
        catch (FormatException)
        {
            throw new RequestBuildException($"'{method}' is not a valid HTTP method.");
        }
    }

    private static void ApplyVersion(HttpRequestMessage message, string? version)
    {
        (message.Version, message.VersionPolicy) = version switch
        {
            "1.1" => (HttpVersion.Version11, HttpVersionPolicy.RequestVersionExact),
            "2" => (HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact),
            "3" => (HttpVersion.Version30, HttpVersionPolicy.RequestVersionExact),
            _ => (HttpVersion.Version20, HttpVersionPolicy.RequestVersionOrLower),
        };
    }

    private static void ApplyAuth(HttpRequestMessage message, AuthConfig auth)
    {
        switch (auth)
        {
            case BasicAuth basic:
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basic.Username}:{basic.Password}"));
                message.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                break;

            case BearerAuth bearer:
                var value = string.IsNullOrWhiteSpace(bearer.Prefix) ? bearer.Token.Trim() : $"{bearer.Prefix.Trim()} {bearer.Token.Trim()}";
                message.Headers.TryAddWithoutValidation("Authorization", value);
                break;

            case ApiKeyAuth { Location: ApiKeyLocation.Header } apiKey when apiKey.Key.Length > 0:
                AddHeader(message, apiKey.Key.Trim(), apiKey.Value);
                break;

            // Windows auth is negotiated by the handler (see HandlerPool); query API keys are added in BuildUri.
        }
    }

    /// <summary>User headers replace headers of the same name set by auth or the body.</summary>
    private static void ApplyHeaders(HttpRequestMessage message, IEnumerable<KeyValueItem> headers)
    {
        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers.Where(h => h.IsActive))
        {
            var name = header.Key.Trim();
            if (replaced.Add(name))
            {
                TryRemove(message.Headers, name);
                if (message.Content is not null) TryRemove(message.Content.Headers, name);
            }
            AddHeader(message, name, header.Value);
        }
    }

    /// <summary>Remove throws for names that belong to the other header collection (request vs content).</summary>
    private static void TryRemove(HttpHeaders headers, string name)
    {
        try { headers.Remove(name); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { }
    }

    private static void AddHeader(HttpRequestMessage message, string name, string value)
    {
        if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
        {
            message.Headers.Host = value;
            return;
        }

        try
        {
            if (message.Headers.TryAddWithoutValidation(name, value)) return;

            // Content-* headers live on the content; send an empty body if there is none.
            message.Content ??= new ByteArrayContent([]);
            if (!message.Content.Headers.TryAddWithoutValidation(name, value))
                throw new RequestBuildException($"Header '{name}' cannot be set.");
        }
        catch (FormatException)
        {
            throw new RequestBuildException($"'{name}' is not a valid header name.");
        }
    }

    private static HttpContent? BuildContent(BodyConfig body) => body switch
    {
        RawBody raw => new StringContent(raw.Content, Encoding.UTF8, raw.MediaType),
        FormUrlEncodedBody form => new FormUrlEncodedContent(
            form.Fields.Where(f => f.IsActive).Select(f => KeyValuePair.Create(f.Key, f.Value))),
        MultipartBody multipart => BuildMultipart(multipart),
        BinaryFileBody binary => BuildFileContent(binary.FilePath, binary.ContentType),
        GraphQlBody graphql => new StringContent(GraphQlJson(graphql), Encoding.UTF8, "application/json"),
        _ => null,
    };

    /// <summary>The JSON a GraphQL server expects; variables must be a JSON object.</summary>
    public static string GraphQlJson(GraphQlBody body)
    {
        var payload = new System.Text.Json.Nodes.JsonObject { ["query"] = body.Query };
        if (body.Variables.Trim().Length > 0)
        {
            try
            {
                payload["variables"] = System.Text.Json.Nodes.JsonNode.Parse(body.Variables, documentOptions: new() { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new RequestBuildException($"GraphQL variables are not valid JSON: {ex.Message}");
            }
        }
        if (!string.IsNullOrWhiteSpace(body.OperationName)) payload["operationName"] = body.OperationName.Trim();
        return payload.ToJsonString();
    }

    private static MultipartFormDataContent BuildMultipart(MultipartBody body)
    {
        var content = new MultipartFormDataContent();
        try
        {
            foreach (var part in body.Parts.Where(p => p.Enabled && p.Name.Length > 0))
            {
                if (part.Kind == MultipartPartKind.File)
                {
                    content.Add(BuildFileContent(part.Value, part.ContentType), part.Name, Path.GetFileName(part.Value));
                }
                else
                {
                    var text = new StringContent(part.Value, Encoding.UTF8);
                    text.Headers.ContentType = string.IsNullOrWhiteSpace(part.ContentType) ? null : ParseMediaType(part.ContentType);
                    content.Add(text, part.Name);
                }
            }
            return content;
        }
        catch
        {
            content.Dispose();
            throw;
        }
    }

    private static StreamContent BuildFileContent(string path, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new RequestBuildException("Choose a file for the request body.");
        if (!File.Exists(path)) throw new RequestBuildException($"File not found: {path}");

        var content = new StreamContent(File.OpenRead(path));
        content.Headers.ContentType = ParseMediaType(string.IsNullOrWhiteSpace(contentType) ? MediaTypes.FromFileName(path) : contentType);
        return content;
    }

    private static MediaTypeHeaderValue ParseMediaType(string value) =>
        MediaTypeHeaderValue.TryParse(value, out var parsed)
            ? parsed
            : throw new RequestBuildException($"'{value}' is not a valid content type.");
}
