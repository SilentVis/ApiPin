using System.Collections.Concurrent;
using System.Net;
using ApiPin.Core.Models;

namespace ApiPin.Core.Http;

/// <summary>
/// One <see cref="HttpClient"/> per distinct connection setup, reused across requests so sockets
/// are pooled while each request still gets its own TLS, redirect and credential settings.
/// </summary>
public sealed class HandlerPool : IDisposable
{
    private readonly ConcurrentDictionary<HandlerKey, HttpClient> _clients = new();

    public HttpClient GetClient(RequestSettings settings, AuthConfig auth)
    {
        var key = new HandlerKey(
            settings.VerifyTls,
            settings.FollowRedirects,
            Math.Max(1, settings.MaxRedirects),
            auth is WindowsAuth w ? new WindowsKey(w.UseCurrentUser, w.Username, w.Password, w.Domain) : null);

        return _clients.GetOrAdd(key, static k => new HttpClient(CreateHandler(k), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // the executor applies the per-request timeout
        });
    }

    private static SocketsHttpHandler CreateHandler(HandlerKey key)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = key.FollowRedirects,
            MaxAutomaticRedirections = key.MaxRedirects,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        if (!key.VerifyTls)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        if (key.Windows is { } w)
        {
            handler.Credentials = w.UseCurrentUser
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(w.Username, w.Password, w.Domain);
        }

        return handler;
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
        _clients.Clear();
    }

    private sealed record HandlerKey(bool VerifyTls, bool FollowRedirects, int MaxRedirects, WindowsKey? Windows);

    private sealed record WindowsKey(bool UseCurrentUser, string Username, string Password, string Domain);
}
