using System.Security.Cryptography;
using ApiPin.Core.Http;
using ApiPin.Core.Models;
using ApiPin.Core.Variables;

namespace ApiPin.IntegrationTests;

/// <summary>Every body and auth type, sent by the real executor to the real EchoServer.</summary>
public sealed class EchoServerTests(EchoServerFixture server) : IClassFixture<EchoServerFixture>, IDisposable
{
    private readonly HandlerPool _pool = new();
    private readonly List<string> _tempFiles = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _pool.Dispose();
        foreach (var file in _tempFiles) File.Delete(file);
    }

    private async Task<ResponseResult> Send(RequestDefinition request, int maxBodyBytes = 10 * 1024 * 1024)
    {
        var result = await new HttpExecutor(_pool, new HttpExecutorOptions { MaxBodyBytes = maxBodyBytes }).SendAsync(request, Ct);
        Assert.Null(result.Error);
        return result;
    }

    private string TempFile(byte[] content, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"apipin-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    // ---------- Bodies ----------

    [Fact]
    public async Task No_body()
    {
        var echo = EchoServerFixture.Json((await Send(new RequestDefinition { Url = $"{server.BaseUrl}/echo" })).Body);

        Assert.Equal("GET", echo.GetProperty("method").GetString());
        Assert.Equal(0, echo.GetProperty("body").GetProperty("length").GetInt32());
    }

    [Theory]
    [InlineData(RawLanguage.Json, "{\"name\":\"Ada\"}", "application/json")]
    [InlineData(RawLanguage.Xml, "<a>1</a>", "application/xml")]
    [InlineData(RawLanguage.Text, "hello", "text/plain")]
    [InlineData(RawLanguage.Html, "<p>hi</p>", "text/html")]
    [InlineData(RawLanguage.JavaScript, "let a = 1;", "application/javascript")]
    public async Task Raw_bodies(RawLanguage language, string content, string mediaType)
    {
        var result = await Send(new RequestDefinition { Method = "POST", Url = $"{server.BaseUrl}/echo", Body = new RawBody { Language = language, Content = content } });

        var body = EchoServerFixture.Json(result.Body).GetProperty("body");
        Assert.StartsWith(mediaType, body.GetProperty("contentType").GetString());
        Assert.Equal(content, body.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Form_url_encoded_body()
    {
        var request = new RequestDefinition
        {
            Method = "POST",
            Url = $"{server.BaseUrl}/echo",
            Body = new FormUrlEncodedBody { Fields = [new() { Key = "a", Value = "1 2" }, new() { Key = "b", Value = "x&y" }, new() { Key = "off", Value = "z", Enabled = false }] },
        };

        var body = EchoServerFixture.Json((await Send(request)).Body).GetProperty("body");

        Assert.Equal("application/x-www-form-urlencoded", body.GetProperty("contentType").GetString());
        Assert.Equal("a=1+2&b=x%26y", body.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Multipart_body_with_text_and_file_parts()
    {
        var bytes = RandomNumberGenerator.GetBytes(3000);
        var file = TempFile(bytes, ".png");
        var request = new RequestDefinition
        {
            Method = "POST",
            Url = $"{server.BaseUrl}/upload",
            Body = new MultipartBody
            {
                Parts =
                [
                    new() { Name = "title", Value = "Picture" },
                    new() { Name = "photo", Kind = MultipartPartKind.File, Value = file },
                    new() { Name = "skipped", Value = "no", Enabled = false },
                ],
            },
        };

        var upload = EchoServerFixture.Json((await Send(request)).Body);

        Assert.StartsWith("multipart/form-data; boundary=", upload.GetProperty("contentType").GetString());
        Assert.Equal("Picture", upload.GetProperty("fields").GetProperty("title")[0].GetString());
        Assert.False(upload.GetProperty("fields").TryGetProperty("skipped", out _));
        var part = upload.GetProperty("files")[0];
        Assert.Equal("photo", part.GetProperty("name").GetString());
        Assert.Equal(Path.GetFileName(file), part.GetProperty("fileName").GetString());
        Assert.Equal("image/png", part.GetProperty("contentType").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), part.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Binary_file_body()
    {
        var bytes = RandomNumberGenerator.GetBytes(5000);
        var request = new RequestDefinition
        {
            Method = "PUT",
            Url = $"{server.BaseUrl}/upload",
            Body = new BinaryFileBody { FilePath = TempFile(bytes, ".bin"), ContentType = "application/x-custom" },
        };

        var upload = EchoServerFixture.Json((await Send(request)).Body);

        Assert.Equal("application/x-custom", upload.GetProperty("contentType").GetString());
        Assert.Equal(5000, upload.GetProperty("length").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), upload.GetProperty("sha256").GetString());
    }

    // ---------- Auth ----------

    [Fact]
    public async Task No_auth_is_rejected_by_protected_endpoints()
    {
        foreach (var path in new[] { "/auth/basic", "/auth/bearer", "/auth/apikey" })
            Assert.Equal(401, (await Send(new RequestDefinition { Url = server.BaseUrl + path })).StatusCode);
    }

    [Theory]
    [InlineData("user", "pass", 200)]
    [InlineData("user", "wrong", 401)]
    public async Task Basic_auth(string user, string password, int expected)
    {
        var result = await Send(new RequestDefinition { Url = $"{server.BaseUrl}/auth/basic", Auth = new BasicAuth { Username = user, Password = password } });
        Assert.Equal(expected, result.StatusCode);
    }

    [Fact]
    public async Task Bearer_auth()
    {
        var ok = await Send(new RequestDefinition { Url = $"{server.BaseUrl}/auth/bearer?token=abc", Auth = new BearerAuth { Token = "abc" } });
        var wrongPrefix = await Send(new RequestDefinition { Url = $"{server.BaseUrl}/auth/bearer", Auth = new BearerAuth { Token = "abc", Prefix = "Token" } });

        Assert.Equal(200, ok.StatusCode);
        Assert.Equal(401, wrongPrefix.StatusCode);
    }

    [Theory]
    [InlineData(ApiKeyLocation.Header, "header")]
    [InlineData(ApiKeyLocation.Query, "query")]
    public async Task Api_key_auth(ApiKeyLocation location, string via)
    {
        var key = location == ApiKeyLocation.Header ? "X-API-Key" : "api_key";
        var result = await Send(new RequestDefinition { Url = $"{server.BaseUrl}/auth/apikey", Auth = new ApiKeyAuth { Key = key, Value = "apipin-key", Location = location } });

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(via, EchoServerFixture.Json(result.Body).GetProperty("via").GetString());
    }

    // ---------- Connection behaviour ----------

    [Fact]
    public async Task Follows_redirects_or_stops_at_the_first()
    {
        var followed = await Send(new RequestDefinition { Url = $"{server.BaseUrl}/redirect/3?x=1" });
        var stopped = await Send(new RequestDefinition { Url = $"{server.BaseUrl}/redirect/3", Settings = new RequestSettings { FollowRedirects = false } });

        Assert.Equal(200, followed.StatusCode);
        Assert.EndsWith("/echo?x=1", followed.Request!.Url);
        Assert.Equal(302, stopped.StatusCode);
        Assert.Contains(stopped.Headers, h => h.Key == "Location" && h.Value == "/redirect/2");
    }

    [Fact]
    public async Task Https_with_a_self_signed_certificate_needs_verification_off()
    {
        var verified = await new HttpExecutor(_pool).SendAsync(new RequestDefinition { Url = $"{server.HttpsUrl}/echo" }, Ct);
        var unverified = await Send(new RequestDefinition { Url = $"{server.HttpsUrl}/echo", Settings = new RequestSettings { VerifyTls = false } });

        Assert.Equal(ErrorKind.Tls, verified.Error?.Kind);
        Assert.Equal(200, unverified.StatusCode);
    }

    [Fact]
    public async Task Times_out_slow_responses()
    {
        var result = await new HttpExecutor(_pool).SendAsync(new RequestDefinition { Url = $"{server.BaseUrl}/delay/3000", Settings = new RequestSettings { TimeoutMs = 200 } }, Ct);
        Assert.Equal(ErrorKind.Timeout, result.Error?.Kind);
    }

    [Fact]
    public async Task Reads_cookies_and_status_codes()
    {
        var cookies = await Send(new RequestDefinition { Url = $"{server.BaseUrl}/cookies/set?session=s1&theme=dark" });
        var teapot = await Send(new RequestDefinition { Url = $"{server.BaseUrl}/status/418" });

        Assert.Equal(["session", "theme"], cookies.Cookies.Select(c => c.Name).Order());
        Assert.All(cookies.Cookies, c => Assert.True(c.HttpOnly));
        Assert.Equal(418, teapot.StatusCode);
    }

    [Fact]
    public async Task Truncates_large_bodies_but_reports_the_full_size()
    {
        var result = await Send(new RequestDefinition { Url = $"{server.BaseUrl}/large/1" }, maxBodyBytes: 100_000);

        Assert.True(result.BodyTruncated);
        Assert.Equal(100_000, result.Body.Length);
        Assert.True(result.SizeBytes > 100_000);
    }

    [Fact]
    public async Task Resolved_variables_reach_the_server()
    {
        var vars = VariableContext.Empty.With(VariableSource.Environment, "Local",
        [
            new Variable { Key = "baseUrl", Value = server.BaseUrl },
            new Variable { Key = "user", Value = "user" },
            new Variable { Key = "password", Value = "pass", Secret = true },
        ]);
        var request = new RequestDefinition
        {
            Method = "POST",
            Url = "{{baseUrl}}/echo?id={{$randomInt}}",
            Headers = [new() { Key = "X-User", Value = "{{user}}" }],
            Auth = new BasicAuth { Username = "{{user}}", Password = "{{password}}" },
            Body = new RawBody { Content = "{\"user\":\"{{user}}\"}" },
        };

        var echo = EchoServerFixture.Json((await Send(VariableResolver.ResolveRequest(request, vars))).Body);

        Assert.Equal("user", echo.GetProperty("headers").GetProperty("X-User")[0].GetString());
        Assert.Equal("pass", echo.GetProperty("auth").GetProperty("password").GetString());
        Assert.Equal("{\"user\":\"user\"}", echo.GetProperty("body").GetProperty("content").GetString());
        Assert.True(int.TryParse(echo.GetProperty("query").GetProperty("id")[0].GetString(), out _));
    }

    [Fact]
    public async Task Custom_methods_and_restricted_headers()
    {
        var request = new RequestDefinition
        {
            Method = "PURGE",
            Url = $"{server.BaseUrl}/echo",
            Headers = [new() { Key = "Host", Value = "api.example.test" }, new() { Key = "Cookie", Value = "a=1" }],
        };

        var echo = EchoServerFixture.Json((await Send(request)).Body);

        Assert.Equal("PURGE", echo.GetProperty("method").GetString());
        Assert.Equal("api.example.test", echo.GetProperty("headers").GetProperty("Host")[0].GetString());
        Assert.Equal("1", echo.GetProperty("cookies").GetProperty("a").GetString());
    }
}
