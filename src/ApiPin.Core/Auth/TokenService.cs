using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiPin.Core.Http;
using ApiPin.Core.Models;

namespace ApiPin.Core.Auth;

/// <summary>Runs the Azure CLI; replaceable for tests.</summary>
public interface IAzureCliRunner
{
    /// <summary>Returns stdout, or throws <see cref="AuthTokenException"/> with the CLI's error.</summary>
    Task<string> GetAccessTokenJsonAsync(string resource, string? tenant, CancellationToken cancellationToken);
}

/// <summary>
/// Gets, caches and refreshes access tokens for <see cref="OAuth2Auth"/> and <see cref="AzureCliAuth"/>.
/// Pass auth configs with variables already resolved; the cache is keyed by those values.
/// </summary>
public sealed class TokenService(HandlerPool pool, IAuthorizationBrowser? browser = null, IAzureCliRunner? azureCli = null, TimeProvider? clock = null)
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, TokenInfo> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly IAuthorizationBrowser _browser = browser ?? new SystemBrowser();
    private readonly IAzureCliRunner _azureCli = azureCli ?? new AzureCliRunner();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Raised when a token is obtained or cleared.</summary>
    public event Action? Changed;

    public static bool Handles(AuthConfig auth) => auth is OAuth2Auth or AzureCliAuth;

    /// <summary>The cached token for this config, valid or not.</summary>
    public TokenInfo? Cached(AuthConfig auth) => _cache.GetValueOrDefault(Key(auth));

    public void Clear(AuthConfig auth)
    {
        _cache.TryRemove(Key(auth), out _);
        Changed?.Invoke();
    }

    public void ClearAll()
    {
        _cache.Clear();
        Changed?.Invoke();
    }

    /// <summary>A valid token: from the cache, by refreshing, or by running the flow again.</summary>
    /// <param name="settings">Connection settings for the token request (TLS verification, client certificate).</param>
    /// <param name="forceNew">Skip the cache and refresh token, e.g. for "Get new token".</param>
    /// <exception cref="AuthTokenException">The token endpoint refused or couldn't be reached.</exception>
    public async Task<TokenInfo> GetTokenAsync(AuthConfig auth, RequestSettings settings, bool forceNew = false, CancellationToken cancellationToken = default)
    {
        var key = Key(auth);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.GetUtcNow();
            var cached = _cache.GetValueOrDefault(key);
            if (!forceNew && cached is not null && !cached.IsExpired(now)) return cached;

            TokenInfo token;
            if (auth is OAuth2Auth oauth && !forceNew && cached?.RefreshToken is { Length: > 0 } refresh)
            {
                try
                {
                    token = await Refresh(oauth, settings, refresh, cancellationToken);
                }
                catch (AuthTokenException)
                {
                    token = await Acquire(auth, settings, cancellationToken); // refresh token expired or revoked
                }
            }
            else
            {
                token = await Acquire(auth, settings, cancellationToken);
            }

            _cache[key] = token;
            Changed?.Invoke();
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    private Task<TokenInfo> Acquire(AuthConfig auth, RequestSettings settings, CancellationToken ct) => auth switch
    {
        OAuth2Auth { GrantType: OAuth2GrantType.ClientCredentials } o => TokenRequest(o, settings, [new("grant_type", "client_credentials")], "client credentials", ct),
        OAuth2Auth { GrantType: OAuth2GrantType.Password } o => TokenRequest(o, settings,
            [new("grant_type", "password"), new("username", o.Username), new("password", o.Password)], "password", ct),
        OAuth2Auth { GrantType: OAuth2GrantType.AuthorizationCode } o => AuthorizationCode(o, settings, ct),
        AzureCliAuth a => AzureCli(a, ct),
        _ => throw new AuthTokenException("Unsupported auth", $"{auth.GetType().Name} doesn't use tokens."),
    };

    private Task<TokenInfo> Refresh(OAuth2Auth o, RequestSettings settings, string refreshToken, CancellationToken ct) =>
        TokenRequest(o, settings, [new("grant_type", "refresh_token"), new("refresh_token", refreshToken)], "refresh token", ct, keepRefreshToken: refreshToken);

    private async Task<TokenInfo> AuthorizationCode(OAuth2Auth o, RequestSettings settings, CancellationToken ct)
    {
        if (!Uri.TryCreate(o.AuthorizationUrl.Trim(), UriKind.Absolute, out var authorize))
            throw new AuthTokenException("Missing authorization URL", "Enter the provider's authorize endpoint for the authorization code flow.");
        if (!Uri.TryCreate(o.RedirectUri.Trim(), UriKind.Absolute, out var redirect))
            throw new AuthTokenException("Invalid redirect URI", $"'{o.RedirectUri}' is not a URL.");

        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", o.ClientId),
            new("redirect_uri", redirect.AbsoluteUri),
            new("state", state),
        };
        if (o.Scope.Trim().Length > 0) query.Add(new("scope", o.Scope.Trim()));
        if (o.Audience.Trim().Length > 0) query.Add(new("audience", o.Audience.Trim()));
        if (o.UsePkce)
        {
            query.Add(new("code_challenge", Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))));
            query.Add(new("code_challenge_method", "S256"));
        }
        var url = new Uri(authorize.AbsoluteUri + (authorize.Query.Length > 0 ? "&" : "?") +
            string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));

        var callback = await LoopbackListener.WaitForRedirectAsync(redirect, () => _browser.OpenAsync(url, ct), SignInTimeout, ct);
        var result = ParseQuery(callback.Query);

        if (result.TryGetValue("error", out var error))
            throw new AuthTokenException("Sign-in failed", $"{error}: {result.GetValueOrDefault("error_description") ?? "the provider refused the request."}");
        if (result.GetValueOrDefault("state") != state)
            throw new AuthTokenException("Sign-in failed", "The state in the redirect doesn't match; the response may not be for this request.");
        if (result.GetValueOrDefault("code") is not { Length: > 0 } code)
            throw new AuthTokenException("Sign-in failed", "The redirect has no authorization code.");

        List<KeyValuePair<string, string>> form = [new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirect.AbsoluteUri)];
        if (o.UsePkce) form.Add(new("code_verifier", verifier));
        return await TokenRequest(o, settings, form, "authorization code", ct);
    }

    private async Task<TokenInfo> TokenRequest(OAuth2Auth o, RequestSettings settings, List<KeyValuePair<string, string>> form, string grant, CancellationToken ct, string? keepRefreshToken = null)
    {
        if (!Uri.TryCreate(UrlTools.EnsureScheme(o.TokenUrl.Trim()), UriKind.Absolute, out var tokenUrl) || o.TokenUrl.Trim().Length == 0)
            throw new AuthTokenException("Missing token URL", "Enter the provider's token endpoint in the Auth tab.");

        if (o.Scope.Trim().Length > 0 && !form.Any(f => f.Key == "code")) form.Add(new("scope", o.Scope.Trim()));
        if (o.Audience.Trim().Length > 0) form.Add(new("audience", o.Audience.Trim()));

        using var message = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
        var secret = o.ClientSecret;
        if (o.ClientAuthentication == OAuth2ClientAuthentication.BasicHeader && secret.Length > 0)
        {
            var pair = $"{Uri.EscapeDataString(o.ClientId)}:{Uri.EscapeDataString(secret)}";
            message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
        }
        else
        {
            form.Add(new("client_id", o.ClientId));
            if (secret.Length > 0) form.Add(new("client_secret", secret));
        }
        message.Content = new FormUrlEncodedContent(form);
        message.Headers.Accept.ParseAdd("application/json");
        message.Headers.UserAgent.ParseAdd(HttpRequestBuilder.DefaultUserAgent);

        HttpClient client;
        try
        {
            client = pool.GetClient(settings with { FollowRedirects = true }, new NoAuth());
        }
        catch (RequestBuildException ex)
        {
            throw new AuthTokenException("Token request failed", ex.Message);
        }

        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(settings.TimeoutMs > 0 ? settings.TimeoutMs : 30_000);
            response = await client.SendAsync(message, timeout.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new AuthTokenException("Token request failed", $"Couldn't reach {tokenUrl.GetLeftPart(UriPartial.Path)}: {ex.Message}",
                ex.HttpRequestError == HttpRequestError.SecureConnectionError ? "For a local token server with a dev certificate, turn off 'Verify TLS certificate' in Settings." : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AuthTokenException("Token request timed out", $"{tokenUrl.GetLeftPart(UriPartial.Path)} didn't answer in time.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            JsonElement json;
            try
            {
                json = JsonDocument.Parse(body).RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new AuthTokenException("Unexpected token response", $"HTTP {(int)response.StatusCode}: {Truncate(body)}");
            }

            if (!response.IsSuccessStatusCode || !json.TryGetProperty("access_token", out var accessToken))
            {
                var error = Str(json, "error") ?? $"HTTP {(int)response.StatusCode}";
                var description = Str(json, "error_description") ?? Truncate(body);
                throw new AuthTokenException("The token endpoint refused the request", $"{error}: {description}",
                    error == "invalid_client" ? "Check the client ID and secret, and whether they go in the Basic header or the body." : null);
            }

            var now = _clock.GetUtcNow();
            var token = accessToken.GetString() ?? "";
            DateTimeOffset? expires = json.TryGetProperty("expires_in", out var e) && (e.TryGetInt64(out var seconds) || long.TryParse(e.GetString(), out seconds))
                ? now.AddSeconds(seconds)
                : JwtDecoder.Expiry(token);
            return new TokenInfo(token, Str(json, "token_type") ?? "Bearer", now, expires,
                Str(json, "refresh_token") ?? keepRefreshToken, Str(json, "scope") ?? (o.Scope.Trim().Length > 0 ? o.Scope.Trim() : null), $"OAuth 2.0 {grant}");
        }
    }

    private async Task<TokenInfo> AzureCli(AzureCliAuth a, CancellationToken ct)
    {
        if (a.Resource.Trim().Length == 0)
            throw new AuthTokenException("Missing resource", "Enter the API's App ID URI (e.g. api://my-api) or a scope.");

        var output = await _azureCli.GetAccessTokenJsonAsync(a.Resource.Trim(), a.Tenant.Trim() is { Length: > 0 } t ? t : null, ct);
        try
        {
            var json = JsonDocument.Parse(output).RootElement;
            var token = Str(json, "accessToken") ?? throw new JsonException("no accessToken");
            DateTimeOffset? expires = json.TryGetProperty("expires_on", out var on) && on.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : DateTimeOffset.TryParse(Str(json, "expiresOn"), out var local) ? local : JwtDecoder.Expiry(token);
            return new TokenInfo(token, Str(json, "tokenType") ?? "Bearer", _clock.GetUtcNow(), expires, null, a.Resource.Trim(), "Azure CLI");
        }
        catch (JsonException)
        {
            throw new AuthTokenException("Unexpected Azure CLI output", Truncate(output));
        }
    }

    /// <summary>Cache key: the values that decide which token you get (secrets hashed, not kept as text).</summary>
    private static string Key(AuthConfig auth)
    {
        var text = auth switch
        {
            OAuth2Auth o => string.Join("\n", "oauth2", o.GrantType, o.TokenUrl.Trim(), o.AuthorizationUrl.Trim(), o.ClientId, o.ClientSecret,
                o.Scope.Trim(), o.Audience.Trim(), o.Username, o.Password, o.RedirectUri.Trim(), o.ClientAuthentication),
            AzureCliAuth a => string.Join("\n", "azurecli", a.Resource.Trim(), a.Tenant.Trim()),
            _ => auth.GetType().Name,
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .GroupBy(p => Uri.UnescapeDataString(p[0]))
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString((g.First().ElementAtOrDefault(1) ?? "").Replace('+', ' ')));

    private static string? Str(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string text) => text.Length > 300 ? text[..300] + "…" : text;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Runs <c>az account get-access-token</c>.</summary>
public sealed class AzureCliRunner : IAzureCliRunner
{
    public async Task<string> GetAccessTokenJsonAsync(string resource, string? tenant, CancellationToken cancellationToken)
    {
        // A scope (".../.default" or "x/y") uses --scope; an App ID URI or GUID uses --resource.
        var useScope = resource.EndsWith("/.default", StringComparison.OrdinalIgnoreCase) || (resource.Contains('/') && !resource.Contains("://"));
        var args = new List<string> { "account", "get-access-token", "--output", "json", useScope ? "--scope" : "--resource", resource };
        if (tenant is not null) args.AddRange(["--tenant", tenant]);

        // az is a .cmd script on Windows, so it runs through cmd.exe.
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", "az" } }
            : new ProcessStartInfo("az");
        foreach (var a in args) start.ArgumentList.Add(a);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new AuthTokenException("Azure CLI not found", "Install the Azure CLI and run 'az login'.");
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }

            if (process.ExitCode != 0)
            {
                var error = (await stderr).Trim();
                var notFound = error.Contains("is not recognized", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase) && error.Contains("az", StringComparison.Ordinal);
                throw notFound
                    ? new AuthTokenException("Azure CLI not found", "Install the Azure CLI and run 'az login'.")
                    : new AuthTokenException("Azure CLI couldn't get a token", error.Length > 0 ? error : $"az exited with code {process.ExitCode}.",
                        error.Contains("az login", StringComparison.OrdinalIgnoreCase) ? "Run 'az login' in a terminal, then send again." : null);
            }
            return await stdout;
        }
    }
}
