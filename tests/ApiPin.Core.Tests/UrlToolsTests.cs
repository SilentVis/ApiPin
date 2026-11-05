using ApiPin.Core.Http;
using ApiPin.Core.Models;

namespace ApiPin.Core.Tests;

public class UrlToolsTests
{
    [Fact]
    public void ParseQuery_keeps_raw_text_and_repeated_keys()
    {
        var pairs = UrlTools.ParseQuery("a=1&b=%20x&a=2&flag&=v");

        Assert.Equal(
            [new("a", "1"), new("b", "%20x"), new("a", "2"), new("flag", ""), new("", "v")],
            pairs);
    }

    [Fact]
    public void WithQuery_rebuilds_from_enabled_params_and_keeps_fragment()
    {
        var url = UrlTools.WithQuery("http://h/p?old=1#top",
        [
            new KeyValueItem { Key = "a", Value = "1" },
            new KeyValueItem { Key = "off", Value = "x", Enabled = false },
            new KeyValueItem { Key = "flag" },
            new KeyValueItem(),
        ]);

        Assert.Equal("http://h/p?a=1&flag#top", url);
    }

    [Fact]
    public void WithQuery_drops_question_mark_when_no_params()
    {
        Assert.Equal("http://h/p", UrlTools.WithQuery("http://h/p?a=1", []));
    }

    [Fact]
    public void SyncQueryParams_keeps_descriptions_and_disabled_rows()
    {
        List<KeyValueItem> existing =
        [
            new() { Key = "a", Value = "1", Description = "first" },
            new() { Key = "hidden", Value = "x", Enabled = false },
        ];

        var synced = UrlTools.SyncQueryParams("http://h/p?a=2&b=3", existing);

        Assert.Collection(synced,
            p => { Assert.Equal(("a", "2", "first", true), (p.Key, p.Value, p.Description, p.Enabled)); },
            p => { Assert.Equal(("b", "3", null, true), (p.Key, p.Value, p.Description, p.Enabled)); },
            p => { Assert.Equal(("hidden", false), (p.Key, p.Enabled)); });
    }

    [Theory]
    [InlineData("http://localhost:5000/orders/:id/items/:itemId?x=:notParam", new[] { "id", "itemId" })]
    [InlineData("localhost:5000/:id", new[] { "id" })]
    [InlineData("https://api.example.com/users/:id/:id", new[] { "id" })]
    [InlineData("http://localhost:5000", new string[0])]
    [InlineData("http://h/a:b/c", new string[0])]
    public void FindPathParams_ignores_port_query_and_inline_colons(string url, string[] expected)
    {
        Assert.Equal(expected, UrlTools.FindPathParams(url));
    }

    [Fact]
    public void ApplyPathParams_escapes_values_and_leaves_empty_ones()
    {
        var url = UrlTools.ApplyPathParams("http://h:80/orders/:id/:other?q=:id",
        [
            new KeyValueItem { Key = "id", Value = "a b/c" },
            new KeyValueItem { Key = "other" },
        ]);

        Assert.Equal("http://h:80/orders/a%20b%2Fc/:other?q=:id", url);
    }

    [Fact]
    public void SyncPathParams_keeps_values_by_name()
    {
        var synced = UrlTools.SyncPathParams("http://h/:b/:c", [new() { Key = "a", Value = "1" }, new() { Key = "b", Value = "2" }]);

        Assert.Equal([("b", "2"), ("c", "")], synced.Select(p => (p.Key, p.Value)));
    }

    [Theory]
    [InlineData("localhost:5000/x", "http://localhost:5000/x")]
    [InlineData("https://h", "https://h")]
    [InlineData("api.example.com", "http://api.example.com")]
    public void EnsureScheme_adds_http_only_when_missing(string input, string expected)
    {
        Assert.Equal(expected, UrlTools.EnsureScheme(input));
    }
}
