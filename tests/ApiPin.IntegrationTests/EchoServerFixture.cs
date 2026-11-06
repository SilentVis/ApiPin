using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ApiPin.EchoServer;
using Microsoft.AspNetCore.Builder;

namespace ApiPin.IntegrationTests;

/// <summary>
/// The real EchoServer on random ports: HTTP, plus HTTPS with a throwaway self-signed certificate.
/// URLs use 127.0.0.1, not localhost: on Windows a refused attempt on ::1 first costs about two seconds.
/// </summary>
public sealed class EchoServerFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = "";
    public string HttpsUrl { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var app = EchoApp.Build(new EchoServerOptions { Host = "localhost", HttpPort = 0, HttpsPort = 0, Quiet = true, Certificate = SelfSigned() });
        await app.StartAsync();
        _app = app;

        var urls = app.Urls.ToList();
        BaseUrl = urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));
        HttpsUrl = urls.First(u => u.StartsWith("https://", StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    public static JsonElement Json(byte[] body) => JsonDocument.Parse(body).RootElement;

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Re-import so Schannel gets a persisted private key on Windows.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }
}
