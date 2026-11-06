// ApiPin.EchoServer — a small API for trying ApiPin against, and for the integration tests.
// Usage: dotnet run -- [port]                      (HTTP on 5080, HTTPS on 7090 when a dev certificate exists)
//        dotnet run -- --port 9000 --https-port 0 --host 0.0.0.0
// See EchoApp.cs for the endpoints.

using ApiPin.EchoServer;

var options = new EchoServerOptions
{
    Host = GetArg("--host") ?? "localhost",
    HttpPort = int.Parse(GetArg("--port") ?? args.FirstOrDefault(a => int.TryParse(a, out _)) ?? "5080"),
    HttpsPort = int.Parse(GetArg("--https-port") ?? "7090"),
};

var app = EchoApp.Build(options, args);

Console.WriteLine("Echo server listening on:");
foreach (var url in EchoApp.ListenUrls(options, app)) Console.WriteLine($"  {url}");
if (options.HttpsPort > 0 && !EchoApp.HasHttps(app))
    Console.WriteLine($"  (HTTPS on {options.HttpsPort} is off: no ASP.NET dev certificate. Run `dotnet dev-certs https --trust` to add one.)");
Console.WriteLine("Endpoints: /echo /status/{code} /delay/{ms} /redirect/{n} /auth/basic /auth/bearer /auth/apikey");
Console.WriteLine("           /cookies /cookies/set /upload /large/{mb} /certs/client /items /oauth/token /oauth/authorize /oauth/me — anything else echoes");
Console.WriteLine("Press Ctrl+C to stop");
app.Run();

string? GetArg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
