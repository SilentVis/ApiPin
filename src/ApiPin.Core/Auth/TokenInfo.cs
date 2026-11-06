using System.Text;
using System.Text.Json;

namespace ApiPin.Core.Auth;

/// <summary>An access token and what came with it.</summary>
public sealed record TokenInfo(
    string AccessToken,
    string TokenType,
    DateTimeOffset ObtainedAt,
    DateTimeOffset? ExpiresAt,
    string? RefreshToken,
    string? Scope,
    string Source)
{
    /// <summary>Tokens are renewed this long before they expire, so one doesn't run out mid-request.</summary>
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } at && now >= at - ExpiryMargin;

    /// <summary>The decoded header and payload when the token is a JWT; null otherwise.</summary>
    public (JsonElement Header, JsonElement Payload)? Jwt => JwtDecoder.TryDecode(AccessToken);
}

/// <summary>Reads (but doesn't validate) JWTs, for showing claims.</summary>
public static class JwtDecoder
{
    public static (JsonElement Header, JsonElement Payload)? TryDecode(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        return Segment(parts[0]) is { } header && Segment(parts[1]) is { } payload ? (header, payload) : null;
    }

    /// <summary>The <c>exp</c> claim of a JWT, when there is one.</summary>
    public static DateTimeOffset? Expiry(string token) =>
        TryDecode(token) is { } jwt && jwt.Payload.ValueKind == JsonValueKind.Object &&
        jwt.Payload.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static JsonElement? Segment(string segment)
    {
        try
        {
            var b64 = segment.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Getting a token failed; <see cref="Exception.Message"/> explains why for the user.</summary>
public sealed class AuthTokenException(string title, string message, string? hint = null) : Exception(message)
{
    public string Title { get; } = title;
    public string? Hint { get; } = hint;
}
