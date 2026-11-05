using ApiPin.Core.Models;

namespace ApiPin.Core.Http;

public static class SetCookieParser
{
    /// <summary>Parses one Set-Cookie header value for display. Unknown attributes are ignored.</summary>
    public static ResponseCookie Parse(string header)
    {
        var parts = header.Split(';', StringSplitOptions.TrimEntries);
        var (name, value) = SplitPair(parts[0]);

        string? domain = null, path = null, expires = null, maxAge = null, sameSite = null;
        bool httpOnly = false, secure = false;

        foreach (var attribute in parts.Skip(1))
        {
            var (key, val) = SplitPair(attribute);
            switch (key.ToLowerInvariant())
            {
                case "domain": domain = val; break;
                case "path": path = val; break;
                case "expires": expires = val; break;
                case "max-age": maxAge = val; break;
                case "samesite": sameSite = val; break;
                case "httponly": httpOnly = true; break;
                case "secure": secure = true; break;
            }
        }

        return new ResponseCookie(name, value, domain, path, expires, maxAge, httpOnly, secure, sameSite);
    }

    private static (string Key, string Value) SplitPair(string text)
    {
        var eq = text.IndexOf('=');
        return eq >= 0 ? (text[..eq].Trim(), text[(eq + 1)..].Trim()) : (text.Trim(), "");
    }
}
