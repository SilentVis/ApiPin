// ApiPin.EchoServer — answers every request with a JSON description of the request itself.
// Usage: dotnet run -- [port]        (default 5080, e.g. `dotnet run -- 9000`)
//        dotnet run -- --port 9000 --host 0.0.0.0

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

var port = GetArg("--port") ?? args.FirstOrDefault(a => int.TryParse(a, out _)) ?? "5080";
var host = GetArg("--host") ?? "localhost";

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls($"http://{host}:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

app.Run(async ctx =>
{
    var req = ctx.Request;

    using var buffer = new MemoryStream();
    await req.Body.CopyToAsync(buffer, ctx.RequestAborted);
    var bodyBytes = buffer.ToArray();
    var isText = IsTextContent(req.ContentType) || (req.ContentType is null && IsValidUtf8(bodyBytes));

    var echo = new
    {
        method = req.Method,
        url = $"{req.Scheme}://{req.Host}{req.PathBase}{req.Path}{req.QueryString}",
        path = req.Path.Value,
        queryString = req.QueryString.Value,
        query = req.Query.ToDictionary(q => q.Key, q => q.Value.ToArray()),
        protocol = req.Protocol,
        remoteAddress = $"{ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort}",
        auth = DescribeAuth(req.Headers.Authorization.ToString()),
        headers = req.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray()),
        body = new
        {
            contentType = req.ContentType,
            length = bodyBytes.Length,
            encoding = bodyBytes.Length == 0 ? null : isText ? "utf-8" : "base64",
            content = bodyBytes.Length == 0 ? null : isText ? Encoding.UTF8.GetString(bodyBytes) : Convert.ToBase64String(bodyBytes),
            json = TryParseJson(req.ContentType, bodyBytes),
        },
    };

    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {req.Method} {req.Path}{req.QueryString} ({bodyBytes.Length} bytes)");

    ctx.Response.ContentType = "application/json; charset=utf-8";
    await ctx.Response.WriteAsync(JsonSerializer.Serialize(echo, json), ctx.RequestAborted);
});

Console.WriteLine($"Echo server listening on http://{host}:{port} — press Ctrl+C to stop");
app.Run();

string? GetArg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static object? DescribeAuth(string header)
{
    if (string.IsNullOrWhiteSpace(header)) return null;

    var parts = header.Split(' ', 2, StringSplitOptions.TrimEntries);
    var scheme = parts[0];
    var value = parts.Length > 1 ? parts[1] : "";

    if (scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            var sep = decoded.IndexOf(':');
            return new
            {
                scheme,
                raw = header,
                username = sep >= 0 ? decoded[..sep] : decoded,
                password = sep >= 0 ? decoded[(sep + 1)..] : null,
            };
        }
        catch (FormatException)
        {
            return new { scheme, raw = header, error = "Invalid Base64 credentials" };
        }
    }

    if (scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
    {
        var segments = value.Split('.');
        return new
        {
            scheme,
            raw = header,
            token = value,
            jwtHeader = segments.Length == 3 ? DecodeJwtSegment(segments[0]) : null,
            jwtPayload = segments.Length == 3 ? DecodeJwtSegment(segments[1]) : null,
        };
    }

    return new { scheme, raw = header, parameter = value };
}

static JsonElement? DecodeJwtSegment(string segment)
{
    try
    {
        var b64 = segment.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        return JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(b64));
    }
    catch (Exception e) when (e is FormatException or JsonException)
    {
        return null;
    }
}

static JsonElement? TryParseJson(string? contentType, byte[] body)
{
    if (body.Length == 0 || contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true) return null;
    try { return JsonSerializer.Deserialize<JsonElement>(body); }
    catch (JsonException) { return null; }
}

static bool IsTextContent(string? contentType) =>
    contentType is not null &&
    (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
     contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
     contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
     contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
     contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
     contentType.Contains("graphql", StringComparison.OrdinalIgnoreCase));

static bool IsValidUtf8(byte[] bytes)
{
    try { new UTF8Encoding(false, true).GetString(bytes); return true; }
    catch (ArgumentException) { return false; }
}
