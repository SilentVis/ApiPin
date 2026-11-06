using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using ApiPin.Core.Auth;
using ApiPin.Core.Models;

namespace ApiPin.Core.Http;

public sealed class HttpExecutorOptions
{
    /// <summary>Bodies above this size are cut off for display; the full size is still reported.</summary>
    public int MaxBodyBytes { get; set; } = 10 * 1024 * 1024;
}

public interface IHttpExecutor
{
    /// <param name="cookies">Cookie jar: its cookies are sent (unless the request sets a Cookie header) and Set-Cookie responses update it.</param>
    Task<ResponseResult> SendAsync(RequestDefinition request, System.Net.CookieContainer? cookies, CancellationToken cancellationToken = default);

    Task<ResponseResult> SendAsync(RequestDefinition request, CancellationToken cancellationToken = default) => SendAsync(request, null, cancellationToken);
}

/// <param name="tokens">Gets OAuth 2.0 / Azure CLI tokens; without it those auth types fail with a message.</param>
public sealed class HttpExecutor(HandlerPool pool, HttpExecutorOptions? options = null, TokenService? tokens = null) : IHttpExecutor
{
    private readonly HttpExecutorOptions _options = options ?? new();

    /// <summary>Sends a request whose inheritance and variables are already resolved (see <see cref="Execution.RequestPreparer"/>).</summary>
    public Task<ResponseResult> SendAsync(RequestDefinition request, CancellationToken cancellationToken = default) => SendAsync(request, null, cancellationToken);

    public async Task<ResponseResult> SendAsync(RequestDefinition request, System.Net.CookieContainer? cookies, CancellationToken cancellationToken = default)
    {
        // Pipeline step 3: token-based auth becomes a bearer header.
        if (TokenService.Handles(request.Auth))
        {
            if (tokens is null)
                return ResponseResult.Failed(new ExecutionError(ErrorKind.Auth, "Token auth isn't available", "This host has no token service."), null, TimingInfo.Zero);
            try
            {
                var token = await tokens.GetTokenAsync(request.Auth, request.Settings, cancellationToken: cancellationToken);
                var prefix = request.Auth is OAuth2Auth o ? o.HeaderPrefix : "Bearer";
                request = request.Clone(newId: false);
                request.Auth = new BearerAuth { Token = token.AccessToken, Prefix = prefix };
            }
            catch (AuthTokenException ex)
            {
                return ResponseResult.Failed(new ExecutionError(ErrorKind.Auth, ex.Title, ex.Message, ex.Hint), null, TimingInfo.Zero);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ResponseResult.Failed(new ExecutionError(ErrorKind.Cancelled, "Request cancelled", "Cancelled while getting a token."), null, TimingInfo.Zero);
            }
        }

        HttpRequestMessage message;
        HttpClient client;
        try
        {
            message = HttpRequestBuilder.Build(request);
        }
        catch (RequestBuildException ex)
        {
            return ResponseResult.Failed(new ExecutionError(ErrorKind.InvalidRequest, "Invalid request", ex.Message), null, TimingInfo.Zero);
        }

        using (message)
        {
            if (cookies is not null && request.Settings.UseCookieJar && message.RequestUri is { } target && !message.Headers.Contains("Cookie"))
            {
                var jarCookies = cookies.GetCookieHeader(target);
                if (jarCookies.Length > 0) message.Headers.TryAddWithoutValidation("Cookie", jarCookies);
            }
            var sent = DescribeSent(message);
            try
            {
                client = pool.GetClient(request.Settings, request.Auth);
            }
            catch (RequestBuildException ex)
            {
                return ResponseResult.Failed(new ExecutionError(ErrorKind.InvalidRequest, "Client certificate problem", ex.Message), sent, TimingInfo.Zero);
            }

            using var timeout = new CancellationTokenSource();
            if (request.Settings.TimeoutMs > 0) timeout.CancelAfter(request.Settings.TimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var stopwatch = Stopwatch.StartNew();
            TimeSpan ttfb = TimeSpan.Zero;
            try
            {
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                ttfb = stopwatch.Elapsed;

                var (body, size, truncated) = await ReadBodyAsync(response.Content, linked.Token);
                var total = stopwatch.Elapsed;

                if (cookies is not null && request.Settings.UseCookieJar && response.Headers.TryGetValues("Set-Cookie", out var toStore))
                {
                    var from = response.RequestMessage?.RequestUri ?? message.RequestUri!;
                    foreach (var value in toStore)
                    {
                        try { cookies.SetCookies(from, value); }
                        catch (System.Net.CookieException) { } // malformed or for another domain: shown, not stored
                    }
                }

                var headers = response.Headers.Concat(response.Content.Headers)
                    .SelectMany(h => h.Value.Select(v => KeyValuePair.Create(h.Key, v)))
                    .ToList();

                return new ResponseResult
                {
                    StatusCode = (int)response.StatusCode,
                    ReasonPhrase = response.ReasonPhrase,
                    HttpVersion = FormatVersion(response.Version),
                    Headers = headers,
                    Cookies = response.Headers.TryGetValues("Set-Cookie", out var setCookies)
                        ? setCookies.Select(SetCookieParser.Parse).ToList()
                        : [],
                    Body = body,
                    BodyTruncated = truncated,
                    SizeBytes = size,
                    ContentType = response.Content.Headers.ContentType?.ToString(),
                    Timings = new TimingInfo(total, ttfb, total - ttfb),
                    Request = sent with { Url = response.RequestMessage?.RequestUri?.ToString() ?? sent.Url },
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Fail(new ExecutionError(ErrorKind.Cancelled, "Request cancelled", "The request was cancelled before it finished."));
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return Fail(new ExecutionError(ErrorKind.Timeout, "Request timed out",
                    $"No complete response within {request.Settings.TimeoutMs:N0} ms.",
                    "Raise the timeout in the Settings tab if the endpoint is slow."));
            }
            catch (HttpRequestException ex)
            {
                return Fail(MapError(ex, message.RequestUri));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
            {
                return Fail(new ExecutionError(ErrorKind.Network, "Request failed", ex.Message));
            }

            ResponseResult Fail(ExecutionError error) =>
                ResponseResult.Failed(error, sent, new TimingInfo(stopwatch.Elapsed, ttfb, TimeSpan.Zero));
        }
    }

    private async Task<(byte[] Body, long Size, bool Truncated)> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            var room = _options.MaxBodyBytes - (int)buffer.Length;
            buffer.Write(chunk, 0, Math.Min(read, room));
            if (read > room)
            {
                var size = content.Headers.ContentLength ?? buffer.Length;
                return (buffer.ToArray(), Math.Max(size, buffer.Length + 1), true);
            }
        }
        return (buffer.ToArray(), buffer.Length, false);
    }

    private static SentRequestInfo DescribeSent(HttpRequestMessage message)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (message.RequestUri is { } uri)
            headers.Add(KeyValuePair.Create("Host", message.Headers.Host ?? uri.Authority));

        foreach (var h in message.Headers.NonValidated.Where(h => h.Key != "Host"))
            headers.AddRange(h.Value.Select(v => KeyValuePair.Create(h.Key, v)));

        long? length = null;
        if (message.Content is { } content)
        {
            length = content.Headers.ContentLength;
            foreach (var h in content.Headers.NonValidated)
                headers.AddRange(h.Value.Select(v => KeyValuePair.Create(h.Key, v)));
        }

        var version = message.VersionPolicy == HttpVersionPolicy.RequestVersionOrLower ? "auto" : FormatVersion(message.Version);
        return new SentRequestInfo(message.Method.Method, message.RequestUri?.ToString() ?? "", version, headers, length);
    }

    private static ExecutionError MapError(HttpRequestException ex, Uri? uri)
    {
        var host = uri?.Host ?? "the server";
        var detail = Innermost(ex).Message;

        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => new(ErrorKind.DnsFailure, "Could not resolve host",
                $"The name '{host}' could not be resolved.", "Check the host name and your network or VPN connection."),

            HttpRequestError.ConnectionError when ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
                new(ErrorKind.ConnectionRefused, "Connection refused",
                    $"Nothing is listening on {uri?.Authority ?? host}.", "Is the API running, and on this port?"),

            HttpRequestError.SecureConnectionError => new(ErrorKind.Tls, "TLS/SSL handshake failed", detail,
                ex.InnerException is AuthenticationException
                    ? "For a local dev certificate, turn off 'Verify TLS certificate' in the Settings tab."
                    : null),

            _ => new(ErrorKind.Network, "Request failed", detail),
        };
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }

    private static string FormatVersion(Version version) => version.Major >= 2 ? version.Major.ToString() : version.ToString(2);
}
