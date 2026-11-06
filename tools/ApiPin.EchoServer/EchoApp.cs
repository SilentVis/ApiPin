using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebUtilities;

namespace ApiPin.EchoServer;

public sealed record EchoServerOptions
{
    /// <summary>"localhost", an IP address, or "0.0.0.0" for every interface.</summary>
    public string Host { get; init; } = "localhost";

    /// <summary>0 picks a free port (handy for tests).</summary>
    public int HttpPort { get; init; } = 5080;

    /// <summary>HTTPS with the ASP.NET Core dev certificate; 0 or a missing certificate turns it off.</summary>
    public int HttpsPort { get; init; } = 7090;

    /// <summary>Don't print a line per request.</summary>
    public bool Quiet { get; init; }

    /// <summary>Certificate for HTTPS instead of the dev certificate (tests use a self-signed one).</summary>
    public X509Certificate2? Certificate { get; init; }
}

/// <summary>
/// The echo server's endpoints. Fixed demo credentials:
/// Basic <c>user</c> / <c>pass</c>, API key <c>apipin-key</c>, OAuth client <c>apipin</c> / <c>secret</c>,
/// OAuth user <c>user</c> / <c>pass</c>.
/// </summary>
public static class EchoApp
{
    public const string BasicUser = "user";
    public const string BasicPassword = "pass";
    public const string ApiKey = "apipin-key";
    public const string ClientId = "apipin";
    public const string ClientSecret = "secret";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static WebApplication Build(EchoServerOptions options, string[]? args = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<OAuthServer>();

        var certificate = options.Certificate ?? (options.HttpsPort > 0 ? FindDevCertificate() : null);
        builder.Services.AddSingleton(new EchoRuntime(certificate is not null));
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            Listen(kestrel, options.Host, options.HttpPort, null);
            if (certificate is not null && (options.HttpsPort > 0 || options.Certificate is not null))
                Listen(kestrel, options.Host, options.HttpsPort, certificate);
        });

        var app = builder.Build();
        app.MapEchoEndpoints();
        return app;
    }

    public static bool HasHttps(WebApplication app) => app.Services.GetRequiredService<EchoRuntime>().Https;

    private sealed record EchoRuntime(bool Https);

    /// <summary>The URLs the server listens on: the real ports once started, the configured ones before.</summary>
    public static IEnumerable<string> ListenUrls(EchoServerOptions options, WebApplication app)
    {
        var bound = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (bound is { Count: > 0 }) return bound;

        var host = options.Host is "0.0.0.0" or "*" ? "localhost" : options.Host;
        var urls = new List<string> { $"http://{host}:{options.HttpPort}" };
        if (HasHttps(app)) urls.Add($"https://{host}:{options.HttpsPort}");
        return urls;
    }

    private static void Listen(KestrelServerOptions kestrel, string host, int port, X509Certificate2? certificate)
    {
        // HTTPS accepts (but doesn't require) a client certificate, so mutual TLS can be tried with /certs/client.
        Action<ListenOptions> configure = certificate is null ? _ => { } : o => o.UseHttps(certificate, https =>
        {
            https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.AllowCertificate;
            https.AllowAnyClientCertificate();
        });
        if (host == "localhost" && port != 0) kestrel.ListenLocalhost(port, configure);
        else if (host is "localhost") kestrel.Listen(IPAddress.Loopback, port, configure);
        else if (host is "0.0.0.0" or "*") kestrel.ListenAnyIP(port, configure);
        else kestrel.Listen(IPAddress.Parse(host), port, configure);
    }

    /// <summary>The certificate created by <c>dotnet dev-certs https</c>, if there is one.</summary>
    private static X509Certificate2? FindDevCertificate()
    {
        const string AspNetHttpsOid = "1.3.6.1.4.1.311.84.1.1";
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates
                .Where(c => c.Extensions.Any(e => e.Oid?.Value == AspNetHttpsOid) && c.HasPrivateKey && c.NotAfter > DateTime.Now)
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault();
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static IEndpointRouteBuilder MapEchoEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map("/echo/{**rest}", Echo);
        app.Map("/status/{code:int}", StatusCode);
        app.Map("/delay/{ms:int}", async (int ms, HttpContext ctx) =>
        {
            await Task.Delay(Math.Clamp(ms, 0, 120_000), ctx.RequestAborted);
            await Echo(ctx);
        });
        app.Map("/redirect/{n:int}", (int n, HttpContext ctx) =>
            Results.Redirect(n <= 1 ? "/echo" + ctx.Request.QueryString : $"/redirect/{n - 1}{ctx.Request.QueryString}"));
        app.Map("/redirect-to", (string url, int? status) =>
            status is >= 300 and < 400 ? Results.Redirect(url, permanent: status is 301 or 308, preserveMethod: status is 307 or 308) : Results.Redirect(url));

        app.Map("/auth/basic", BasicAuth);
        app.Map("/auth/bearer", BearerAuth);
        app.Map("/auth/apikey", ApiKeyAuth);

        app.Map("/cookies", Cookies);
        app.Map("/cookies/set", (HttpContext ctx) =>
        {
            foreach (var (name, value) in ctx.Request.Query)
                ctx.Response.Cookies.Append(name, value.ToString(), new CookieOptions { Path = "/", HttpOnly = true });
            return Cookies(ctx);
        });
        app.Map("/cookies/delete", (HttpContext ctx) =>
        {
            foreach (var name in ctx.Request.Query.Keys) ctx.Response.Cookies.Delete(name, new CookieOptions { Path = "/" });
            return Results.Json(new { deleted = ctx.Request.Query.Keys }, Json);
        });

        app.Map("/upload", (Delegate)Upload);
        app.Map("/certs/client", (HttpContext ctx) => ctx.Connection.ClientCertificate is { } cert
            ? Results.Json(new { presented = true, subject = cert.Subject, issuer = cert.Issuer, thumbprint = cert.Thumbprint, notAfter = cert.NotAfter }, Json)
            : Results.Json(new { presented = false, hint = ctx.Request.IsHttps ? "No client certificate was sent." : "Use the HTTPS port." }, Json, statusCode: 401));
        app.Map("/large/{mb:double}", Large);

        app.MapPost("/oauth/token", (HttpContext ctx, OAuthServer oauth) => oauth.Token(ctx));
        app.MapGet("/oauth/authorize", (HttpContext ctx, OAuthServer oauth) => oauth.Authorize(ctx));
        app.Map("/oauth/me", (HttpContext ctx, OAuthServer oauth) => oauth.Me(ctx));

        app.MapFallback(Echo);
        return app;
    }

    // ---------- Echo ----------

    private static async Task Echo(HttpContext ctx)
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
            cookies = req.Cookies.ToDictionary(c => c.Key, c => c.Value),
            clientCertificate = ctx.Connection.ClientCertificate?.Subject,
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

        Log(ctx, $"{req.Method} {req.Path}{req.QueryString} ({bodyBytes.Length} bytes)");
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(echo, Json), ctx.RequestAborted);
    }

    private static IResult StatusCode(int code, HttpContext ctx)
    {
        Log(ctx, $"{ctx.Request.Method} /status/{code}");
        if (code is < 100 or > 599) return Results.BadRequest(new { error = "Status code must be 100-599." });
        if (code is 204 or 304 || code < 200) return Results.StatusCode(code);
        var reason = ReasonPhrases.GetReasonPhrase(code);
        return Results.Json(new { status = code, description = reason }, Json, statusCode: code);
    }

    // ---------- Auth checks ----------

    private static IResult BasicAuth(HttpContext ctx, string? user, string? password)
    {
        var (expectedUser, expectedPassword) = (user ?? BasicUser, password ?? BasicPassword);
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                if (decoded == $"{expectedUser}:{expectedPassword}")
                    return Results.Json(new { authenticated = true, user = expectedUser }, Json);
            }
            catch (FormatException)
            {
            }
        }
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"ApiPin Echo\"";
        return Results.Json(new { authenticated = false, hint = $"Use Basic auth with {expectedUser} / {expectedPassword}." }, Json, statusCode: 401);
    }

    private static IResult BearerAuth(HttpContext ctx, string? token)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var sent = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        if (!string.IsNullOrEmpty(sent) && (token is null || sent == token))
            return Results.Json(new { authenticated = true, token = sent }, Json);

        ctx.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Json(new { authenticated = false, hint = token is null ? "Send any Bearer token." : $"Send Bearer {token}." }, Json, statusCode: 401);
    }

    private static IResult ApiKeyAuth(HttpContext ctx)
    {
        var fromHeader = ctx.Request.Headers["X-API-Key"].ToString();
        var fromQuery = ctx.Request.Query["api_key"].ToString();
        var key = fromHeader.Length > 0 ? fromHeader : fromQuery;
        if (key == ApiKey)
            return Results.Json(new { authenticated = true, via = fromHeader.Length > 0 ? "header" : "query" }, Json);
        return Results.Json(new { authenticated = false, hint = $"Send X-API-Key: {ApiKey} or ?api_key={ApiKey}." }, Json, statusCode: 401);
    }

    // ---------- Cookies, uploads, large bodies ----------

    private static IResult Cookies(HttpContext ctx) =>
        Results.Json(new { cookies = ctx.Request.Cookies.ToDictionary(c => c.Key, c => c.Value) }, Json);

    private static async Task<IResult> Upload(HttpContext ctx)
    {
        var req = ctx.Request;
        if (!req.HasFormContentType)
        {
            using var buffer = new MemoryStream();
            await req.Body.CopyToAsync(buffer, ctx.RequestAborted);
            var bytes = buffer.ToArray();
            return Results.Json(new { contentType = req.ContentType, length = bytes.Length, sha256 = Sha256(bytes) }, Json);
        }

        var form = await req.ReadFormAsync(ctx.RequestAborted);
        var files = new List<object>();
        foreach (var file in form.Files)
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ctx.RequestAborted);
            files.Add(new { name = file.Name, fileName = file.FileName, contentType = file.ContentType, length = file.Length, sha256 = Sha256(buffer.ToArray()) });
        }
        Log(ctx, $"{req.Method} /upload ({form.Count} fields, {files.Count} files)");
        return Results.Json(new { contentType = req.ContentType, fields = form.ToDictionary(f => f.Key, f => f.Value.ToArray()), files }, Json);
    }

    /// <summary>A JSON array of roughly <paramref name="mb"/> megabytes, streamed.</summary>
    private static async Task Large(double mb, HttpContext ctx)
    {
        var target = (long)(Math.Clamp(mb, 0, 500) * 1024 * 1024);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var stream = ctx.Response.Body;
        await stream.WriteAsync("["u8.ToArray(), ctx.RequestAborted);
        long written = 1;
        for (var i = 0; written < target; i++)
        {
            var item = Encoding.UTF8.GetBytes($"{(i == 0 ? "" : ",")}\n  {{\"id\": {i}, \"name\": \"Item {i}\", \"tags\": [\"a\", \"b\"], \"price\": {i % 1000}.99, \"active\": {(i % 2 == 0 ? "true" : "false")}}}");
            await stream.WriteAsync(item, ctx.RequestAborted);
            written += item.Length;
        }
        await stream.WriteAsync("\n]"u8.ToArray(), ctx.RequestAborted);
    }

    // ---------- Helpers ----------

    private static void Log(HttpContext ctx, string line)
    {
        if (!ctx.RequestServices.GetRequiredService<EchoServerOptions>().Quiet)
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static object? DescribeAuth(string header)
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

    internal static JsonElement? DecodeJwtSegment(string segment)
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

    private static JsonElement? TryParseJson(string? contentType, byte[] body)
    {
        if (body.Length == 0 || contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(body); }
        catch (JsonException) { return null; }
    }

    private static bool IsTextContent(string? contentType) =>
        contentType is not null &&
        (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
         contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
         contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
         contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
         contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
         contentType.Contains("graphql", StringComparison.OrdinalIgnoreCase));

    private static bool IsValidUtf8(byte[] bytes)
    {
        try { new UTF8Encoding(false, true).GetString(bytes); return true; }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>
/// A fake OAuth 2.0 server for trying token flows: client credentials, password, refresh token and
/// authorization code with PKCE (auto-approved, no login page). Tokens are HS256 JWTs signed with a per-run key.
/// </summary>
internal sealed class OAuthServer
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new();
    private readonly ConcurrentDictionary<string, RefreshGrant> _refreshTokens = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>GET /oauth/authorize — approves at once and redirects back with a code.</summary>
    public IResult Authorize(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var redirect = q["redirect_uri"].ToString();
        if (q["response_type"] != "code") return Results.BadRequest(new { error = "unsupported_response_type" });
        if (q["client_id"] != EchoApp.ClientId) return Results.BadRequest(new { error = "invalid_client" });
        if (!Uri.TryCreate(redirect, UriKind.Absolute, out _)) return Results.BadRequest(new { error = "invalid_request", error_description = "redirect_uri is required." });

        var code = Token(24);
        _codes[code] = new PendingCode(redirect, q["code_challenge"].ToString(), q["code_challenge_method"].ToString(), q["scope"].ToString(), DateTimeOffset.UtcNow.AddMinutes(5));

        var separator = redirect.Contains('?') ? '&' : '?';
        var state = q["state"].ToString();
        return Results.Redirect($"{redirect}{separator}code={Uri.EscapeDataString(code)}{(state.Length > 0 ? "&state=" + Uri.EscapeDataString(state) : "")}");
    }

    /// <summary>POST /oauth/token — form body; client credentials in the body or a Basic header. ?lifetime=seconds shortens tokens.</summary>
    public async Task<IResult> Token(HttpContext ctx)
    {
        if (!ctx.Request.HasFormContentType) return Error("invalid_request", "Send application/x-www-form-urlencoded.");
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);

        var (clientId, clientSecret) = ClientCredentials(ctx, form);
        if (clientId != EchoApp.ClientId) return Error("invalid_client", $"Unknown client '{clientId}'. Use {EchoApp.ClientId}.", 401);

        var lifetime = int.TryParse(ctx.Request.Query["lifetime"], out var seconds) && seconds > 0 ? seconds : 3600;
        var scope = form["scope"].ToString();
        var audience = form["audience"].ToString();

        switch (form["grant_type"].ToString())
        {
            case "client_credentials":
                if (clientSecret != EchoApp.ClientSecret) return Error("invalid_client", "Wrong client secret.", 401);
                return Issue(clientId, clientId, scope, audience, lifetime, refresh: false);

            case "password":
                if (clientSecret != EchoApp.ClientSecret) return Error("invalid_client", "Wrong client secret.", 401);
                if (form["username"] != EchoApp.BasicUser || form["password"] != EchoApp.BasicPassword)
                    return Error("invalid_grant", "Wrong username or password.");
                return Issue(clientId, EchoApp.BasicUser, scope, audience, lifetime, refresh: true);

            case "authorization_code":
                if (!_codes.TryRemove(form["code"].ToString(), out var pending) || pending.Expires < DateTimeOffset.UtcNow)
                    return Error("invalid_grant", "Unknown or expired code.");
                if (form["redirect_uri"] != pending.RedirectUri) return Error("invalid_grant", "redirect_uri does not match.");
                if (pending.Challenge.Length > 0 && !VerifyPkce(form["code_verifier"].ToString(), pending))
                    return Error("invalid_grant", "PKCE verification failed.");
                if (pending.Challenge.Length == 0 && clientSecret != EchoApp.ClientSecret)
                    return Error("invalid_client", "Wrong client secret.", 401);
                return Issue(clientId, EchoApp.BasicUser, scope.Length > 0 ? scope : pending.Scope, audience, lifetime, refresh: true);

            case "refresh_token":
                if (!_refreshTokens.TryRemove(form["refresh_token"].ToString(), out var grant))
                    return Error("invalid_grant", "Unknown refresh token.");
                return Issue(clientId, grant.Subject, grant.Scope, audience, lifetime, refresh: true);

            default:
                return Error("unsupported_grant_type", "Use client_credentials, password, authorization_code or refresh_token.");
        }
    }

    /// <summary>A protected resource: requires a valid, unexpired token issued by this server.</summary>
    public IResult Me(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : "";
        if (Validate(token) is not { } claims)
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            return Results.Json(new { error = "invalid_token" }, Json, statusCode: 401);
        }
        return Results.Json(new { authenticated = true, claims }, Json);
    }

    private IResult Issue(string clientId, string subject, string scope, string audience, int lifetime, bool refresh)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new Dictionary<string, object> { ["sub"] = subject, ["client_id"] = clientId, ["iat"] = now, ["exp"] = now + lifetime, ["iss"] = "apipin-echo" };
        if (scope.Length > 0) payload["scope"] = scope;
        if (audience.Length > 0) payload["aud"] = audience;

        var response = new Dictionary<string, object>
        {
            ["access_token"] = Sign(payload),
            ["token_type"] = "Bearer",
            ["expires_in"] = lifetime,
        };
        if (scope.Length > 0) response["scope"] = scope;
        if (refresh)
        {
            var refreshToken = Token(32);
            _refreshTokens[refreshToken] = new RefreshGrant(subject, scope);
            response["refresh_token"] = refreshToken;
        }
        return Results.Json(response, Json);
    }

    private string Sign(Dictionary<string, object> payload)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signature = Base64Url(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{header}.{body}")));
        return $"{header}.{body}.{signature}";
    }

    private JsonElement? Validate(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        var expected = Base64Url(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}")));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[2]))) return null;
        if (EchoApp.DecodeJwtSegment(parts[1]) is not { } claims) return null;
        return claims.TryGetProperty("exp", out var exp) && exp.GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? claims : null;
    }

    private static bool VerifyPkce(string verifier, PendingCode pending)
    {
        if (verifier.Length == 0) return false;
        var computed = pending.ChallengeMethod.Equals("S256", StringComparison.OrdinalIgnoreCase)
            ? Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            : verifier;
        return computed == pending.Challenge;
    }

    private static (string Id, string Secret) ClientCredentials(HttpContext ctx, IFormCollection form)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var sep = decoded.IndexOf(':');
                if (sep >= 0) return (Uri.UnescapeDataString(decoded[..sep]), Uri.UnescapeDataString(decoded[(sep + 1)..]));
            }
            catch (FormatException)
            {
            }
        }
        return (form["client_id"].ToString(), form["client_secret"].ToString());
    }

    private static IResult Error(string error, string description, int status = 400) =>
        Results.Json(new { error, error_description = description }, Json, statusCode: status);

    private static string Token(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record PendingCode(string RedirectUri, string Challenge, string ChallengeMethod, string Scope, DateTimeOffset Expires);

    private sealed record RefreshGrant(string Subject, string Scope);
}
