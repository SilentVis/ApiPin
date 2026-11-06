using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using ApiPin.Core.Models;

namespace ApiPin.Core.Http;

/// <summary>
/// One <see cref="HttpClient"/> per distinct connection setup, reused across requests so sockets
/// are pooled while each request still gets its own TLS, redirect, proxy, certificate and credential settings.
/// </summary>
/// <param name="proxy">The app-wide proxy, read for every request; null means the system proxy.</param>
public sealed class HandlerPool(Func<ProxySettings>? proxy = null) : IDisposable
{
    private readonly ConcurrentDictionary<HandlerKey, HttpClient> _clients = new();

    /// <exception cref="RequestBuildException">The client certificate can't be loaded.</exception>
    public HttpClient GetClient(RequestSettings settings, AuthConfig auth)
    {
        var key = new HandlerKey(
            settings.VerifyTls,
            settings.FollowRedirects,
            Math.Max(1, settings.MaxRedirects),
            auth is WindowsAuth w ? new WindowsKey(w.UseCurrentUser, w.Username, w.Password, w.Domain) : null,
            settings.ClientCertificate is { } c && IsSet(c) ? c with { } : null,
            proxy?.Invoke() ?? new ProxySettings());

        if (_clients.TryGetValue(key, out var existing)) return existing;

        // Load the certificate outside GetOrAdd so a bad file or password surfaces as a friendly error.
        var certificate = key.Certificate is { } cert ? ClientCertificateLoader.Load(cert) : null;
        return _clients.GetOrAdd(key, k => new HttpClient(CreateHandler(k, certificate), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // the executor applies the per-request timeout
        });
    }

    private static bool IsSet(ClientCertificateSettings c) =>
        c.Source == ClientCertificateSource.File ? c.FilePath.Trim().Length > 0 : c.Thumbprint.Trim().Length > 0;

    private static SocketsHttpHandler CreateHandler(HandlerKey key, X509Certificate2? certificate)
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

        if (certificate is not null)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };

        if (key.Windows is { } w)
        {
            handler.Credentials = w.UseCurrentUser
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(w.Username, w.Password, w.Domain);
        }

        ApplyProxy(handler, key.Proxy);
        return handler;
    }

    internal static void ApplyProxy(SocketsHttpHandler handler, ProxySettings settings)
    {
        switch (settings.Mode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;

            case ProxyMode.Custom when Uri.TryCreate(UrlTools.EnsureScheme(settings.Url.Trim()), UriKind.Absolute, out var url):
                var webProxy = new WebProxy(url, settings.BypassLocal, settings.BypassEntries.Select(WildcardToRegex).ToArray());
                if (settings.Username.Length > 0) webProxy.Credentials = new NetworkCredential(settings.Username, settings.Password);
                handler.Proxy = webProxy;
                handler.UseProxy = true;
                break;

            default:
                handler.UseProxy = true; // the system (Windows/WinINET or environment) proxy
                break;
        }
    }

    /// <summary>
    /// "*.corp.local" → a regex WebProxy understands. WebProxy matches against "scheme://host[:port]",
    /// so the scheme and port are optional around the host pattern.
    /// </summary>
    internal static string WildcardToRegex(string pattern) =>
        @"^(?:[a-z][a-z0-9+.\-]*://)?" + Regex.Escape(pattern).Replace(@"\*", ".*") + @"(?::\d+)?$";

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
        _clients.Clear();
    }

    private sealed record HandlerKey(
        bool VerifyTls,
        bool FollowRedirects,
        int MaxRedirects,
        WindowsKey? Windows,
        ClientCertificateSettings? Certificate,
        ProxySettings Proxy);

    private sealed record WindowsKey(bool UseCurrentUser, string Username, string Password, string Domain);
}

/// <summary>Loads client certificates for mutual TLS from a PFX file or the Windows certificate store.</summary>
public static class ClientCertificateLoader
{
    /// <exception cref="RequestBuildException">The certificate is missing, the password is wrong, or it has no private key.</exception>
    public static X509Certificate2 Load(ClientCertificateSettings settings)
    {
        X509Certificate2 certificate;
        if (settings.Source == ClientCertificateSource.File)
        {
            var path = settings.FilePath.Trim();
            if (!File.Exists(path)) throw new RequestBuildException($"Client certificate file not found: {path}");
            try
            {
                certificate = X509CertificateLoader.LoadPkcs12FromFile(path, settings.Password);
            }
            catch (CryptographicException ex)
            {
                throw new RequestBuildException($"Can't open the client certificate '{Path.GetFileName(path)}': {ex.Message} Check the password.");
            }
        }
        else
        {
            certificate = FindInStore(settings.Thumbprint)
                ?? throw new RequestBuildException($"No certificate with thumbprint {settings.Thumbprint.Trim()} in the CurrentUser or LocalMachine 'My' store.");
        }

        if (!certificate.HasPrivateKey)
            throw new RequestBuildException($"The client certificate '{certificate.Subject}' has no private key, so it can't be used for TLS.");
        return certificate;
    }

    private static X509Certificate2? FindInStore(string thumbprint)
    {
        var normalized = new string(thumbprint.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length == 0) return null;
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
                if (found.Count > 0) return found[0];
            }
            catch (CryptographicException)
            {
            }
        }
        return null;
    }
}
