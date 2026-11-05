using System.Text;
using ApiPin.Core.Formatting;

namespace ApiPin.Core.Tests;

public class BodyFormatterTests
{
    [Theory]
    [InlineData("application/json; charset=utf-8", "{}", BodyKind.Json)]
    [InlineData("application/problem+json", "{}", BodyKind.Json)]
    [InlineData("application/xml", "<a/>", BodyKind.Xml)]
    [InlineData("text/html", "<p>", BodyKind.Html)]
    [InlineData("text/plain", "hi", BodyKind.Text)]
    [InlineData("image/png", "x", BodyKind.Image)]
    [InlineData(null, " [1,2]", BodyKind.Json)]
    [InlineData(null, "<!DOCTYPE html><html>", BodyKind.Html)]
    [InlineData(null, "plain", BodyKind.Text)]
    [InlineData(null, "", BodyKind.Empty)]
    public void Detect_uses_content_type_then_sniffs(string? contentType, string body, BodyKind expected)
    {
        Assert.Equal(expected, BodyFormatter.Detect(contentType, Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void Detect_binary_when_not_text()
    {
        Assert.Equal(BodyKind.Binary, BodyFormatter.Detect("application/octet-stream", [0, 1, 0xFF]));
    }

    [Fact]
    public void Pretty_json_is_indented_without_escaping()
    {
        var pretty = BodyFormatter.Pretty("{\"a\":\"<&>\",\"b\":[1]}", BodyKind.Json);

        Assert.Equal("{\n  \"a\": \"<&>\",\n  \"b\": [\n    1\n  ]\n}", pretty.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Pretty_returns_input_when_invalid()
    {
        Assert.Equal("{oops", BodyFormatter.Pretty("{oops", BodyKind.Json));
        Assert.Equal("<a>", BodyFormatter.Pretty("<a>", BodyKind.Xml));
    }

    [Fact]
    public void Decode_honours_charset()
    {
        var latin1 = Encoding.Latin1.GetBytes("café");
        Assert.Equal("café", BodyFormatter.Decode(latin1, "text/plain; charset=iso-8859-1"));
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    public void FormatSize(long bytes, string expected)
    {
        Assert.Equal(expected, BodyFormatter.FormatSize(bytes));
    }
}
