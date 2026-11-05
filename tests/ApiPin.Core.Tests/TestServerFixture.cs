using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApiPin.Core.Tests;

/// <summary>A real Kestrel server on a random port, so the executor is tested over actual sockets.</summary>
public sealed class TestServerFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.Map("/echo", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Json(new
            {
                method = ctx.Request.Method,
                query = ctx.Request.QueryString.Value,
                headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                body,
            });
        });
        app.Map("/status/{code:int}", (int code) => Results.StatusCode(code));
        app.Map("/delay/{ms:int}", async (int ms, CancellationToken ct) =>
        {
            await Task.Delay(ms, ct);
            return Results.Text("done");
        });
        app.Map("/redirect", () => Results.Redirect("/echo?redirected=1"));
        app.Map("/cookies", (HttpContext ctx) =>
        {
            ctx.Response.Headers.Append("Set-Cookie", "a=1; Path=/; HttpOnly");
            ctx.Response.Headers.Append("Set-Cookie", "b=2");
            return Results.Text("ok");
        });
        app.Map("/large/{bytes:int}", (int bytes) => Results.Bytes(new byte[bytes], "application/octet-stream"));

        await app.StartAsync();
        _app = app;
        BaseUrl = app.Urls.First();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    public static JsonElement Json(byte[] body) => JsonDocument.Parse(body).RootElement;
}
