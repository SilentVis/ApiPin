// ApiPin in the browser: Blazor Server hosting the same UI as the desktop app.
// Requests are sent from this server process, so CORS and browser header restrictions don't apply.
//
// It listens on localhost only by default. Anyone who can reach it can make this machine send
// arbitrary HTTP requests, so don't expose it on a network address without putting auth in front.

using ApiPin.Storage;
using ApiPin.Storage.Logging;
using ApiPin.UI;
using ApiPin.Web;

var builder = WebApplication.CreateBuilder(args);

// --urls or ASPNETCORE_URLS (both end up as "urls") override this.
if (builder.Configuration["urls"] is null)
    builder.WebHost.UseUrls("http://localhost:5100");

// Serve the UI library's CSS/JS from the build output in any environment, not only Development.
// Published output doesn't need this (the files are copied to wwwroot), and there it is a no-op.
builder.WebHost.UseStaticWebAssets();

builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(FileLoggerProvider.DefaultDirectory, "web")));

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// A separate file from the desktop app, so both can run at once without overwriting each other's tabs.
// --workspace <folder> opens that workspace instead of the last one.
builder.Services.AddApiPin(
    Path.Combine(Path.GetDirectoryName(ScratchTabStore.DefaultPath)!, "scratch-tabs-web.json"),
    builder.Configuration["workspace"]);

var app = builder.Build();

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
