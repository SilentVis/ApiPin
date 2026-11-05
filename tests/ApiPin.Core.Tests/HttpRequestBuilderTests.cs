using System.Text;
using ApiPin.Core.Http;
using ApiPin.Core.Models;

namespace ApiPin.Core.Tests;

public class HttpRequestBuilderTests
{
    private static RequestDefinition Request(string url = "http://localhost/api", string method = "GET") =>
        new() { Url = url, Method = method };

    [Fact]
    public void Adds_scheme_path_params_and_default_headers()
    {
        var request = Request("localhost:5000/orders/:id?expand=items");
        request.PathParams.Add(new KeyValueItem { Key = "id", Value = "42" });

        using var message = HttpRequestBuilder.Build(request);

        Assert.Equal("http://localhost:5000/orders/42?expand=items", message.RequestUri!.ToString());
        Assert.Equal(HttpRequestBuilder.DefaultUserAgent, message.Headers.UserAgent.ToString());
        Assert.Equal("*/*", message.Headers.Accept.ToString());
    }

    [Theory]
    [InlineData("post", "POST")]
    [InlineData("PURGE", "PURGE")]
    [InlineData("", "GET")]
    public void Accepts_standard_and_custom_methods(string method, string expected)
    {
        using var message = HttpRequestBuilder.Build(Request(method: method));
        Assert.Equal(expected, message.Method.Method);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://")]
    [InlineData("ftp://host/file")]
    public void Rejects_invalid_urls(string url)
    {
        Assert.Throws<RequestBuildException>(() => HttpRequestBuilder.Build(Request(url)));
    }

    [Fact]
    public void Rejects_invalid_method()
    {
        Assert.Throws<RequestBuildException>(() => HttpRequestBuilder.Build(Request(method: "BAD VERB")));
    }

    [Fact]
    public void Basic_auth_encodes_credentials()
    {
        var request = Request();
        request.Auth = new BasicAuth { Username = "alice", Password = "pä:ss" };

        using var message = HttpRequestBuilder.Build(request);

        Assert.Equal("Basic", message.Headers.Authorization!.Scheme);
        Assert.Equal("alice:pä:ss", Encoding.UTF8.GetString(Convert.FromBase64String(message.Headers.Authorization.Parameter!)));
    }

    [Theory]
    [InlineData("Bearer", "abc", "Bearer abc")]
    [InlineData("Token", " abc ", "Token abc")]
    [InlineData("", "abc", "abc")]
    public void Bearer_auth_uses_prefix(string prefix, string token, string expected)
    {
        var request = Request();
        request.Auth = new BearerAuth { Prefix = prefix, Token = token };

        using var message = HttpRequestBuilder.Build(request);

        Assert.Equal(expected, string.Join(",", message.Headers.GetValues("Authorization")));
    }

    [Fact]
    public void Api_key_in_header()
    {
        var request = Request();
        request.Auth = new ApiKeyAuth { Key = "X-API-Key", Value = "secret" };

        using var message = HttpRequestBuilder.Build(request);

        Assert.Equal("secret", Assert.Single(message.Headers.GetValues("X-API-Key")));
    }

    [Fact]
    public void Api_key_in_query_is_escaped()
    {
        var request = Request("http://h/p?a=1");
        request.Auth = new ApiKeyAuth { Key = "api key", Value = "a&b", Location = ApiKeyLocation.Query };

        using var message = HttpRequestBuilder.Build(request);

        Assert.Equal("?a=1&api%20key=a%26b", message.RequestUri!.Query);
    }

    [Fact]
    public void User_headers_override_auth_and_body_headers()
    {
        var request = Request(method: "POST");
        request.Auth = new BearerAuth { Token = "from-auth" };
        request.Body = new RawBody { Language = RawLanguage.Json, Content = "{}" };
        request.Headers.Add(new KeyValueItem { Key = "authorization", Value = "Custom x" });
        request.Headers.Add(new KeyValueItem { Key = "Content-Type", Value = "application/vnd.api+json" });
        request.Headers.Add(new KeyValueItem { Key = "X-Multi", Value = "1" });
        request.Headers.Add(new KeyValueItem { Key = "X-Multi", Value = "2" });
        request.Headers.Add(new KeyValueItem { Key = "X-Off", Value = "no", Enabled = false });
        request.Headers.Add(new KeyValueItem { Key = "Host", Value = "example.test" });

        using var message = HttpRequestBuilder.Build(request);

        Assert.Equal("Custom x", Assert.Single(message.Headers.GetValues("Authorization")));
        Assert.Equal("application/vnd.api+json", message.Content!.Headers.ContentType!.ToString());
        Assert.Equal(["1", "2"], message.Headers.GetValues("X-Multi"));
        Assert.False(message.Headers.Contains("X-Off"));
        Assert.Equal("example.test", message.Headers.Host);
    }

    [Fact]
    public void Content_header_without_body_creates_empty_content()
    {
        var request = Request();
        request.Headers.Add(new KeyValueItem { Key = "Content-Type", Value = "text/plain" });

        using var message = HttpRequestBuilder.Build(request);

        Assert.Equal("text/plain", message.Content!.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData(RawLanguage.Json, "application/json")]
    [InlineData(RawLanguage.Xml, "application/xml")]
    [InlineData(RawLanguage.Text, "text/plain")]
    public async Task Raw_body_sets_media_type(RawLanguage language, string mediaType)
    {
        var request = Request(method: "POST");
        request.Body = new RawBody { Language = language, Content = "hello" };

        using var message = HttpRequestBuilder.Build(request);

        Assert.Equal(mediaType, message.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("hello", await message.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Form_body_encodes_active_fields()
    {
        var request = Request(method: "POST");
        request.Body = new FormUrlEncodedBody
        {
            Fields =
            [
                new() { Key = "a", Value = "1 2" },
                new() { Key = "b", Value = "x&y" },
                new() { Key = "off", Value = "z", Enabled = false },
                new(),
            ],
        };

        using var message = HttpRequestBuilder.Build(request);

        Assert.Equal("application/x-www-form-urlencoded", message.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("a=1+2&b=x%26y", await message.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Multipart_body_contains_text_and_file_parts()
    {
        var file = Path.Combine(Path.GetTempPath(), $"apipin-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, "{\"k\":1}", TestContext.Current.CancellationToken);
        try
        {
            var request = Request(method: "POST");
            request.Body = new MultipartBody
            {
                Parts =
                [
                    new() { Name = "title", Value = "hello" },
                    new() { Name = "doc", Kind = MultipartPartKind.File, Value = file },
                ],
            };

            using var message = HttpRequestBuilder.Build(request);
            var text = await message.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal("multipart/form-data", message.Content.Headers.ContentType!.MediaType);
            Assert.Contains("name=title", text);
            Assert.Contains("hello", text);
            Assert.Contains($"filename={Path.GetFileName(file)}", text);
            Assert.Contains("Content-Type: application/json", text);
            Assert.Contains("{\"k\":1}", text);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Missing_body_file_is_reported()
    {
        var request = Request(method: "POST");
        request.Body = new BinaryFileBody { FilePath = @"C:\does\not\exist.bin" };

        var ex = Assert.Throws<RequestBuildException>(() => HttpRequestBuilder.Build(request));
        Assert.Contains("File not found", ex.Message);
    }

    [Theory]
    [InlineData(null, 2, HttpVersionPolicy.RequestVersionOrLower)]
    [InlineData("1.1", 1, HttpVersionPolicy.RequestVersionExact)]
    [InlineData("2", 2, HttpVersionPolicy.RequestVersionExact)]
    public void Http_version_setting(string? setting, int major, HttpVersionPolicy policy)
    {
        var request = Request();
        request.Settings.HttpVersion = setting;

        using var message = HttpRequestBuilder.Build(request);

        Assert.Equal((major, policy), (message.Version.Major, message.VersionPolicy));
    }
}
