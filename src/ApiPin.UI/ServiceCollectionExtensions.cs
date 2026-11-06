using ApiPin.Core.Auth;
using ApiPin.Core.Http;
using ApiPin.Storage;
using ApiPin.UI.Services;
using ApiPin.UI.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApiPin.UI;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the engine and UI state. Hosts may register their own <see cref="IPlatformServices"/> first.</summary>
    /// <param name="scratchFilePath">Where open tabs are kept; defaults to app data.</param>
    /// <param name="startupWorkspace">Workspace folder to open instead of the last one (e.g. from the command line).</param>
    public static IServiceCollection AddApiPin(this IServiceCollection services, string? scratchFilePath = null, string? startupWorkspace = null)
    {
        services.AddLogging();
        services.AddSingleton(new WorkspaceStartup(startupWorkspace));
        services.AddSingleton(sp => new HandlerPool(() => sp.GetRequiredService<AppSettingsStore>().Load().Proxy));
        services.TryAddSingleton<IAuthorizationBrowser, SystemBrowser>();
        services.AddSingleton(sp => new TokenService(sp.GetRequiredService<HandlerPool>(), sp.GetRequiredService<IAuthorizationBrowser>()));
        services.AddSingleton(new HttpExecutorOptions());
        services.AddSingleton<IHttpExecutor>(sp => new HttpExecutor(sp.GetRequiredService<HandlerPool>(), sp.GetRequiredService<HttpExecutorOptions>(), sp.GetRequiredService<TokenService>()));
        services.AddSingleton(new ScratchTabStore(scratchFilePath ?? ScratchTabStore.DefaultPath));
        services.AddSingleton(new AppSettingsStore(AppSettingsStore.DefaultPath));
        services.AddSingleton<ISecretStore>(OperatingSystem.IsWindows()
            ? new DpapiSecretStore(DpapiSecretStore.DefaultPath)
            : new InMemorySecretStore()); // no DPAPI: secrets last until the app closes
        services.AddSingleton<WorkspaceState>();
        services.TryAddSingleton<IPlatformServices, BrowserPlatformServices>();

        // Per UI session (one per browser connection in the web host).
        services.AddScoped<DialogService>();
        services.AddScoped<UiActions>();
        return services;
    }
}
