using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ApiPin.Core.Auth;
using ApiPin.Core.Http;
using ApiPin.Core.Models;

namespace ApiPin.IntegrationTests;

/// <summary>OAuth 2.0 flows against the EchoServer's fake OAuth server, Azure CLI tokens, client certificates and proxies.</summary>
public sealed class AuthAndConnectionTests(EchoServerFixture server) : IClassFixture<EchoServerFixture>, IDisposable
{
    private readonly HandlerPool _pool = new();
    private readonly List<string> _tempFiles = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _pool.Dispose();
        foreach (var f in _tempFiles) File.Delete(f);
    }

    private OAuth2Auth ClientCredentials(string? query = null) => new()
    {
        GrantType = OAuth2GrantType.ClientCredentials,
        TokenUrl = $"{server.BaseUrl}/oauth/token{query}",
        ClientId = "apipin",
        ClientSecret = "secret",
        Scope = "orders.read",
    };

    private (HttpExecutor Executor, TokenService Tokens) Create(IAuthorizationBrowser? browser = null, IAzureCliRunner? az = null)
    {
        var tokens = new TokenService(_pool, browser, az);
        return (new HttpExecutor(_pool, null, tokens), tokens);
    }

    [Theory]
    [InlineData(OAuth2ClientAuthentication.BasicHeader)]
    [InlineData(OAuth2ClientAuthentication.Body)]
    public async Task Client_credentials_token_is_fetched_once_and_cached(OAuth2ClientAuthentication clientAuth)
    {
        var (executor, tokens) = Create();
        var auth = ClientCredentials();
        auth.ClientAuthentication = clientAuth;
        var request = new RequestDefinition { Url = $"{server.BaseUrl}/oauth/me", Auth = auth };

        var first = await executor.SendAsync(request, Ct);
        var token = tokens.Cached(auth);
        var second = await executor.SendAsync(request, Ct);

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);
        Assert.NotNull(token);
        Assert.Same(token, tokens.Cached(auth)); // not fetched again
        Assert.Equal("orders.read", token.Scope);
        Assert.True(token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.Equal("orders.read", token.Jwt!.Value.Payload.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Wrong_client_secret_is_an_auth_error_with_the_providers_message()
    {
        var (executor, _) = Create();
        var auth = ClientCredentials();
        auth.ClientSecret = "wrong";

        var result = await executor.SendAsync(new RequestDefinition { Url = $"{server.BaseUrl}/oauth/me", Auth = auth }, Ct);

        Assert.Equal(ErrorKind.Auth, result.Error?.Kind);
        Assert.Contains("invalid_client", result.Error!.Detail);
    }

    [Fact]
    public async Task Password_grant_then_refresh_when_expired()
    {
        var (_, tokens) = Create();
        var auth = new OAuth2Auth
        {
            GrantType = OAuth2GrantType.Password,
            TokenUrl = $"{server.BaseUrl}/oauth/token?lifetime=1", // within the expiry margin: expired at once
            ClientId = "apipin",
            ClientSecret = "secret",
            Username = "user",
            Password = "pass",
        };

        var first = await tokens.GetTokenAsync(auth, new RequestSettings(), cancellationToken: Ct);
        var second = await tokens.GetTokenAsync(auth, new RequestSettings(), cancellationToken: Ct);

        Assert.Equal("OAuth 2.0 password", first.Source);
        Assert.NotNull(first.RefreshToken);
        Assert.Equal("OAuth 2.0 refresh token", second.Source);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken); // the server rotates refresh tokens
        Assert.Equal("user", second.Jwt!.Value.Payload.GetProperty("sub").GetString());

        tokens.Clear(auth);
        Assert.Null(tokens.Cached(auth));
    }

    [Fact]
    public async Task Authorization_code_with_pkce_through_a_loopback_redirect()
    {
        var browser = new ScriptedBrowser();
        var (executor, tokens) = Create(browser);
        var auth = new OAuth2Auth
        {
            GrantType = OAuth2GrantType.AuthorizationCode,
            AuthorizationUrl = $"{server.BaseUrl}/oauth/authorize",
            TokenUrl = $"{server.BaseUrl}/oauth/token",
            ClientId = "apipin",
            Scope = "openid profile",
            RedirectUri = $"http://127.0.0.1:{FreePort()}/callback",
            UsePkce = true,
        };

        var result = await executor.SendAsync(new RequestDefinition { Url = $"{server.BaseUrl}/oauth/me", Auth = auth }, Ct);

        Assert.Null(result.Error);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("code_challenge_method=S256", browser.Opened!.Query);
        Assert.Equal("OAuth 2.0 authorization code", tokens.Cached(auth)!.Source);
        Assert.NotNull(tokens.Cached(auth)!.RefreshToken);
    }

    [Fact]
    public async Task Authorization_code_rejects_a_mismatched_state()
    {
        var (_, tokens) = Create(new ScriptedBrowser(tamperState: true));
        var auth = new OAuth2Auth
        {
            GrantType = OAuth2GrantType.AuthorizationCode,
            AuthorizationUrl = $"{server.BaseUrl}/oauth/authorize",
            TokenUrl = $"{server.BaseUrl}/oauth/token",
            ClientId = "apipin",
            RedirectUri = $"http://localhost:{FreePort()}/cb",
        };

        var ex = await Assert.ThrowsAsync<AuthTokenException>(() => tokens.GetTokenAsync(auth, new RequestSettings(), cancellationToken: Ct));
        Assert.Contains("state", ex.Message);
    }

    [Fact]
    public async Task Azure_cli_token_is_sent_as_bearer()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var az = new FakeAzureCli($$"""{ "accessToken": "az-token-123", "expires_on": {{expires}}, "tokenType": "Bearer" }""");
        var (executor, tokens) = Create(az: az);
        var auth = new AzureCliAuth { Resource = "api://orders", Tenant = "contoso.onmicrosoft.com" };

        var result = await executor.SendAsync(new RequestDefinition { Url = $"{server.BaseUrl}/auth/bearer?token=az-token-123", Auth = auth }, Ct);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(("api://orders", "contoso.onmicrosoft.com"), (az.Resource, az.Tenant));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expires), tokens.Cached(auth)!.ExpiresAt);
    }

    [Fact]
    public async Task Client_certificate_from_a_pfx_file()
    {
        var pfx = ClientPfx("apipin-client", "p@ss");
        var settings = new RequestSettings { VerifyTls = false, ClientCertificate = new() { FilePath = pfx, Password = "p@ss" } };

        var result = await new HttpExecutor(_pool).SendAsync(new RequestDefinition { Url = $"{server.HttpsUrl}/certs/client", Settings = settings }, Ct);
        var without = await new HttpExecutor(_pool).SendAsync(new RequestDefinition { Url = $"{server.HttpsUrl}/certs/client", Settings = new RequestSettings { VerifyTls = false } }, Ct);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("CN=apipin-client", EchoServerFixture.Json(result.Body).GetProperty("subject").GetString());
        Assert.Equal(401, without.StatusCode);
    }

    [Fact]
    public async Task Wrong_certificate_password_is_reported()
    {
        var pfx = ClientPfx("apipin-client", "right");
        var settings = new RequestSettings { VerifyTls = false, ClientCertificate = new() { FilePath = pfx, Password = "wrong" } };

        var result = await new HttpExecutor(_pool).SendAsync(new RequestDefinition { Url = $"{server.HttpsUrl}/certs/client", Settings = settings }, Ct);

        Assert.Equal(ErrorKind.InvalidRequest, result.Error?.Kind);
        Assert.Contains("password", result.Error!.Detail);
    }

    [Fact]
    public async Task Requests_go_through_a_custom_proxy()
    {
        using var pool = new HandlerPool(() => new ProxySettings { Mode = ProxyMode.Custom, Url = server.BaseUrl, BypassLocal = false });

        // The echo server plays the proxy: it receives the absolute URL and answers itself.
        var result = await new HttpExecutor(pool).SendAsync(new RequestDefinition { Url = "http://api.example.invalid/echo?via=proxy" }, Ct);

        Assert.Null(result.Error);
        var echo = EchoServerFixture.Json(result.Body);
        Assert.Equal("api.example.invalid", echo.GetProperty("headers").GetProperty("Host")[0].GetString());
        Assert.Equal("proxy", echo.GetProperty("query").GetProperty("via")[0].GetString());
    }

    [Fact]
    public async Task Bypassed_hosts_skip_the_proxy()
    {
        using var pool = new HandlerPool(() => new ProxySettings { Mode = ProxyMode.Custom, Url = "http://127.0.0.1:1", Bypass = "127.0.0.*, *.local", BypassLocal = false });

        var result = await new HttpExecutor(pool).SendAsync(new RequestDefinition { Url = $"{server.BaseUrl}/echo" }, Ct);

        Assert.Null(result.Error);
        Assert.Equal(200, result.StatusCode);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string ClientPfx(string name, string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false)); // client auth
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(Path.GetTempPath(), $"apipin-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>Plays the user's browser: follows the authorize redirect to the loopback listener.</summary>
    private sealed class ScriptedBrowser(bool tamperState = false) : IAuthorizationBrowser
    {
        public Uri? Opened { get; private set; }

        public Task OpenAsync(Uri url, CancellationToken cancellationToken)
        {
            Opened = url;
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                using var authorize = await http.GetAsync(url, cancellationToken);
                var callback = authorize.Headers.Location!.ToString();
                if (tamperState) callback = System.Text.RegularExpressions.Regex.Replace(callback, "state=[^&]+", "state=forged");
                using var _ = await http.GetAsync(callback, cancellationToken);
            }, cancellationToken);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAzureCli(string output) : IAzureCliRunner
    {
        public string? Resource { get; private set; }
        public string? Tenant { get; private set; }

        public Task<string> GetAccessTokenJsonAsync(string resource, string? tenant, CancellationToken cancellationToken)
        {
            (Resource, Tenant) = (resource, tenant);
            return Task.FromResult(output);
        }
    }
}
