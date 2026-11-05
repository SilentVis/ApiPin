using System.Net;
using System.Net.Sockets;
using ApiPin.Core.Http;
using ApiPin.Core.Models;

namespace ApiPin.Core.Tests;

public sealed class HttpExecutorTests(TestServerFixture server) : IClassFixture<TestServerFixture>, IDisposable
{
    private readonly HandlerPool _pool = new();
    private HttpExecutor Executor(int maxBodyBytes = 10 * 1024 * 1024) => new(_pool, new HttpExecutorOptions { MaxBodyBytes = maxBodyBytes });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _pool.Dispose();

    [Fact]
    public async Task Sends_method_query_headers_auth_and_body()
    {
        var request = new RequestDefinition
        {
            Method = "PUT",
            Url = $"{server.BaseUrl}/echo?x=1",
            Headers = [new() { Key = "X-Test", Value = "hello" }],
            Auth = new BearerAuth { Token = "tkn" },
            Body = new RawBody { Content = "{\"a\":1}" },
        };

        var result = await Executor().SendAsync(request, Ct);

        Assert.Null(result.Error);
        Assert.Equal(200, result.StatusCode);
        Assert.StartsWith("application/json", result.ContentType);
        var echo = TestServerFixture.Json(result.Body);
        Assert.Equal("PUT", echo.GetProperty("method").GetString());
        Assert.Equal("?x=1", echo.GetProperty("query").GetString());
        Assert.Equal("hello", echo.GetProperty("headers").GetProperty("X-Test").GetString());
        Assert.Equal("Bearer tkn", echo.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal("{\"a\":1}", echo.GetProperty("body").GetString());
        Assert.Equal(result.Body.Length, result.SizeBytes);
        Assert.True(result.Timings.Total >= result.Timings.TimeToFirstByte);
    }

    [Fact]
    public async Task Reports_sent_request()
    {
        var request = new RequestDefinition { Method = "POST", Url = $"{server.BaseUrl}/echo", Body = new RawBody { Content = "abc" } };

        var result = await Executor().SendAsync(request, Ct);

        Assert.NotNull(result.Request);
        Assert.Equal("POST", result.Request.Method);
        Assert.Equal(3, result.Request.BodyLength);
        Assert.Contains(result.Request.Headers, h => h.Key == "Content-Type" && h.Value.StartsWith("application/json"));
        Assert.Contains(result.Request.Headers, h => h.Key == "Host");
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    public async Task Http_error_status_is_a_response_not_an_error(int code)
    {
        var result = await Executor().SendAsync(new RequestDefinition { Url = $"{server.BaseUrl}/status/{code}" }, Ct);

        Assert.Null(result.Error);
        Assert.Equal(code, result.StatusCode);
    }

    [Fact]
    public async Task Follows_redirects_by_default_and_reports_final_url()
    {
        var result = await Executor().SendAsync(new RequestDefinition { Url = $"{server.BaseUrl}/redirect" }, Ct);

        Assert.Equal(200, result.StatusCode);
        Assert.EndsWith("/echo?redirected=1", result.Request!.Url);
    }

    [Fact]
    public async Task Does_not_follow_redirects_when_disabled()
    {
        var request = new RequestDefinition { Url = $"{server.BaseUrl}/redirect" };
        request.Settings.FollowRedirects = false;

        var result = await Executor().SendAsync(request, Ct);

        Assert.Equal(302, result.StatusCode);
        Assert.Contains(result.Headers, h => h.Key == "Location");
    }

    [Fact]
    public async Task Parses_cookies()
    {
        var result = await Executor().SendAsync(new RequestDefinition { Url = $"{server.BaseUrl}/cookies" }, Ct);

        Assert.Equal(["a", "b"], result.Cookies.Select(c => c.Name));
        Assert.True(result.Cookies[0].HttpOnly);
    }

    [Fact]
    public async Task Truncates_large_bodies_but_reports_full_size()
    {
        var result = await Executor(maxBodyBytes: 1000).SendAsync(new RequestDefinition { Url = $"{server.BaseUrl}/large/5000" }, Ct);

        Assert.True(result.BodyTruncated);
        Assert.Equal(1000, result.Body.Length);
        Assert.Equal(5000, result.SizeBytes);
    }

    [Fact]
    public async Task Timeout_is_reported()
    {
        var request = new RequestDefinition { Url = $"{server.BaseUrl}/delay/5000" };
        request.Settings.TimeoutMs = 200;

        var result = await Executor().SendAsync(request, Ct);

        Assert.Equal(ErrorKind.Timeout, result.Error?.Kind);
    }

    [Fact]
    public async Task Cancellation_is_reported()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(200);

        var result = await Executor().SendAsync(new RequestDefinition { Url = $"{server.BaseUrl}/delay/5000" }, cts.Token);

        Assert.Equal(ErrorKind.Cancelled, result.Error?.Kind);
    }

    [Fact]
    public async Task Connection_refused_is_reported()
    {
        var result = await Executor().SendAsync(new RequestDefinition { Url = $"http://127.0.0.1:{FreePort()}/" }, Ct);

        Assert.Equal(ErrorKind.ConnectionRefused, result.Error?.Kind);
    }

    [Fact]
    public async Task Unknown_host_is_reported()
    {
        var result = await Executor().SendAsync(new RequestDefinition { Url = "http://apipin-does-not-exist.invalid/" }, Ct);

        Assert.Equal(ErrorKind.DnsFailure, result.Error?.Kind);
    }

    [Fact]
    public async Task Invalid_request_is_reported_without_sending()
    {
        var result = await Executor().SendAsync(new RequestDefinition { Url = "" }, Ct);

        Assert.Equal(ErrorKind.InvalidRequest, result.Error?.Kind);
        Assert.Null(result.Request);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
