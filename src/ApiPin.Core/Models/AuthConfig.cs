using System.Text.Json.Serialization;

namespace ApiPin.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoAuth), "none")]
[JsonDerivedType(typeof(InheritAuth), "inherit")]
[JsonDerivedType(typeof(BasicAuth), "basic")]
[JsonDerivedType(typeof(BearerAuth), "bearer")]
[JsonDerivedType(typeof(ApiKeyAuth), "apikey")]
[JsonDerivedType(typeof(WindowsAuth), "windows")]
[JsonDerivedType(typeof(OAuth2Auth), "oauth2")]
[JsonDerivedType(typeof(AzureCliAuth), "azurecli")]
public abstract class AuthConfig
{
    public virtual AuthConfig Clone() => (AuthConfig)MemberwiseClone();

    /// <summary>A copy with <paramref name="map"/> applied to every text field (used to resolve variables).</summary>
    public virtual AuthConfig Map(Func<string, string> map) => Clone();
}

public sealed class NoAuth : AuthConfig;

/// <summary>Use the auth of the nearest folder or collection that sets one; no auth when none does.</summary>
public sealed class InheritAuth : AuthConfig;

public sealed class BasicAuth : AuthConfig
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public override AuthConfig Map(Func<string, string> map) => new BasicAuth { Username = map(Username), Password = map(Password) };
}

public sealed class BearerAuth : AuthConfig
{
    public string Token { get; set; } = "";

    /// <summary>Scheme written before the token; "Bearer" by default, empty to send the token alone.</summary>
    public string Prefix { get; set; } = "Bearer";

    public override AuthConfig Map(Func<string, string> map) => new BearerAuth { Token = map(Token), Prefix = map(Prefix) };
}

public enum ApiKeyLocation { Header, Query }

public sealed class ApiKeyAuth : AuthConfig
{
    public string Key { get; set; } = "X-API-Key";
    public string Value { get; set; } = "";
    public ApiKeyLocation Location { get; set; } = ApiKeyLocation.Header;

    public override AuthConfig Map(Func<string, string> map) => new ApiKeyAuth { Key = map(Key), Value = map(Value), Location = Location };
}

/// <summary>Windows integrated auth (Negotiate / NTLM / Kerberos).</summary>
public sealed class WindowsAuth : AuthConfig
{
    public bool UseCurrentUser { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Domain { get; set; } = "";

    public override AuthConfig Map(Func<string, string> map) =>
        new WindowsAuth { UseCurrentUser = UseCurrentUser, Username = map(Username), Password = map(Password), Domain = map(Domain) };
}

public enum OAuth2GrantType { ClientCredentials, AuthorizationCode, Password }

/// <summary>How the client ID and secret are sent to the token endpoint.</summary>
public enum OAuth2ClientAuthentication { BasicHeader, Body }

/// <summary>
/// OAuth 2.0: ApiPin gets an access token from the token endpoint, caches it until it expires (refreshing it when it
/// can), and sends it as <c>Authorization: Bearer &lt;token&gt;</c>.
/// </summary>
public sealed class OAuth2Auth : AuthConfig
{
    public const string DefaultRedirectUri = "http://localhost:7890/callback";

    public OAuth2GrantType GrantType { get; set; } = OAuth2GrantType.ClientCredentials;
    public string TokenUrl { get; set; } = "";

    /// <summary>Authorization code flow: the provider's authorize endpoint, opened in the system browser.</summary>
    public string AuthorizationUrl { get; set; } = "";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Scope { get; set; } = "";

    /// <summary>Sent as <c>audience</c> (Auth0 and others) when set.</summary>
    public string Audience { get; set; } = "";

    /// <summary>Password grant only.</summary>
    public string Username { get; set; } = "";

    /// <summary>Password grant only.</summary>
    public string Password { get; set; } = "";

    /// <summary>Authorization code flow: a loopback URL ApiPin listens on for the redirect.</summary>
    public string RedirectUri { get; set; } = DefaultRedirectUri;

    public bool UsePkce { get; set; } = true;
    public OAuth2ClientAuthentication ClientAuthentication { get; set; } = OAuth2ClientAuthentication.BasicHeader;

    /// <summary>Written before the token in the Authorization header.</summary>
    public string HeaderPrefix { get; set; } = "Bearer";

    public override AuthConfig Map(Func<string, string> map) => new OAuth2Auth
    {
        GrantType = GrantType,
        TokenUrl = map(TokenUrl),
        AuthorizationUrl = map(AuthorizationUrl),
        ClientId = map(ClientId),
        ClientSecret = map(ClientSecret),
        Scope = map(Scope),
        Audience = map(Audience),
        Username = map(Username),
        Password = map(Password),
        RedirectUri = map(RedirectUri),
        UsePkce = UsePkce,
        ClientAuthentication = ClientAuthentication,
        HeaderPrefix = map(HeaderPrefix),
    };
}

/// <summary>A token from the Azure CLI (<c>az account get-access-token</c>) for APIs secured by Microsoft Entra ID.</summary>
public sealed class AzureCliAuth : AuthConfig
{
    /// <summary>App ID URI or client ID of the API, e.g. <c>api://my-api</c>; or a scope ending in /.default.</summary>
    public string Resource { get; set; } = "";

    /// <summary>Optional tenant ID or domain.</summary>
    public string Tenant { get; set; } = "";

    public override AuthConfig Map(Func<string, string> map) => new AzureCliAuth { Resource = map(Resource), Tenant = map(Tenant) };
}
