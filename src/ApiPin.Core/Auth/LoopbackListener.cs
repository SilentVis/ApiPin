using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ApiPin.Core.Auth;

/// <summary>Opens the authorization page; the host decides how (the system browser by default).</summary>
public interface IAuthorizationBrowser
{
    Task OpenAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>Opens the page in the user's default browser.</summary>
public sealed class SystemBrowser : IAuthorizationBrowser
{
    public Task OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Receives the OAuth redirect on a loopback address (RFC 8252): a tiny HTTP listener on 127.0.0.1 / ::1 that waits
/// for one GET to the redirect path and returns its full URL. Plain sockets, so no URL ACL or admin rights are needed.
/// </summary>
public static class LoopbackListener
{
    /// <param name="redirectUri">An http://localhost:port/path or http://127.0.0.1:port/path URL.</param>
    /// <param name="onListening">Called once listening (open the browser here).</param>
    public static async Task<Uri> WaitForRedirectAsync(Uri redirectUri, Func<Task> onListening, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (redirectUri.Scheme != Uri.UriSchemeHttp || !redirectUri.IsLoopback)
            throw new AuthTokenException("Invalid redirect URI", $"'{redirectUri}' must be an http:// loopback address such as {Models.OAuth2Auth.DefaultRedirectUri}.");

        var listeners = new List<TcpListener>();
        try
        {
            foreach (var address in Addresses(redirectUri))
            {
                var listener = new TcpListener(address, redirectUri.Port);
                try
                {
                    listener.Start();
                    listeners.Add(listener);
                }
                catch (SocketException) when (listeners.Count > 0 || address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    // IPv6 loopback unavailable, or only one family could bind: the other one is enough.
                }
            }
            if (listeners.Count == 0)
                throw new AuthTokenException("Can't listen for the redirect", $"Port {redirectUri.Port} is not available.");

            await onListening();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var accepts = listeners.Select(l => Accept(l, redirectUri, timeoutCts.Token)).ToList();
            try
            {
                return await await Task.WhenAny(accepts);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AuthTokenException("Sign-in timed out", $"No redirect to {redirectUri} within {timeout.TotalMinutes:0} minutes.",
                    "Finish signing in in the browser, and check that this redirect URI is registered with the provider.");
            }
            finally
            {
                timeoutCts.Cancel();
            }
        }
        catch (SocketException ex)
        {
            throw new AuthTokenException("Can't listen for the redirect", $"Port {redirectUri.Port}: {ex.Message}");
        }
        finally
        {
            foreach (var l in listeners) l.Stop();
        }
    }

    private static IEnumerable<IPAddress> Addresses(Uri uri) =>
        uri.Host switch
        {
            "127.0.0.1" => [IPAddress.Loopback],
            "[::1]" or "::1" => [IPAddress.IPv6Loopback],
            _ => [IPAddress.Loopback, IPAddress.IPv6Loopback], // localhost: browsers may try either
        };

    private static async Task<Uri> Accept(TcpListener listener, Uri redirectUri, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            var requestLine = await ReadRequestLine(stream, ct);
            var parts = requestLine.Split(' ');
            var target = parts.Length >= 2 ? parts[1] : "/";
            var path = target.Split('?')[0];

            if (parts[0] == "GET" && string.Equals(path.TrimEnd('/'), redirectUri.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal))
            {
                await Respond(stream, 200, "<h2>Signed in</h2><p>ApiPin received the authorization response. You can close this tab.</p>", ct);
                return new Uri($"{redirectUri.Scheme}://{redirectUri.Authority}{target}");
            }
            await Respond(stream, 404, "<p>Not the ApiPin redirect address.</p>", ct);
        }
    }

    private static async Task<string> ReadRequestLine(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n") || text.Contains("\n\n")) return text.Split('\n')[0].Trim();
        }
        return Encoding.ASCII.GetString(buffer, 0, total).Split('\n')[0].Trim();
    }

    private static async Task Respond(Stream stream, int status, string body, CancellationToken ct)
    {
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>ApiPin</title></head><body style=\"font-family:sans-serif;padding:2em\">{body}</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
    }
}
