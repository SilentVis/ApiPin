using ApiPin.Core.Http;

namespace ApiPin.Core.Tests;

public class SetCookieParserTests
{
    [Fact]
    public void Parses_value_and_attributes()
    {
        var cookie = SetCookieParser.Parse("session=abc=123; Path=/; Domain=example.com; Max-Age=3600; HttpOnly; Secure; SameSite=Lax");

        Assert.Equal("session", cookie.Name);
        Assert.Equal("abc=123", cookie.Value);
        Assert.Equal("/", cookie.Path);
        Assert.Equal("example.com", cookie.Domain);
        Assert.Equal("3600", cookie.MaxAge);
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal("Lax", cookie.SameSite);
    }

    [Fact]
    public void Plain_cookie_has_no_attributes()
    {
        var cookie = SetCookieParser.Parse("a=b");

        Assert.Equal(("a", "b", false, false, null), (cookie.Name, cookie.Value, cookie.HttpOnly, cookie.Secure, cookie.Path));
    }
}
