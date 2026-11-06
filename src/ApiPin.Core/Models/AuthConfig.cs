using System.Text.Json.Serialization;

namespace ApiPin.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoAuth), "none")]
[JsonDerivedType(typeof(BasicAuth), "basic")]
[JsonDerivedType(typeof(BearerAuth), "bearer")]
[JsonDerivedType(typeof(ApiKeyAuth), "apikey")]
[JsonDerivedType(typeof(WindowsAuth), "windows")]
public abstract class AuthConfig
{
    public virtual AuthConfig Clone() => (AuthConfig)MemberwiseClone();

    /// <summary>A copy with <paramref name="map"/> applied to every text field (used to resolve variables).</summary>
    public virtual AuthConfig Map(Func<string, string> map) => Clone();
}

public sealed class NoAuth : AuthConfig;

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
