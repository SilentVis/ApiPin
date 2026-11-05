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
        services.AddSingleton(new WorkspaceStartup(startupWorkspace));
        services.AddSingleton<HandlerPool>();
        services.AddSingleton(new HttpExecutorOptions());
        services.AddSingleton<IHttpExecutor, HttpExecutor>();
        services.AddSingleton(new ScratchTabStore(scratchFilePath ?? ScratchTabStore.DefaultPath));
        services.AddSingleton(new AppSettingsStore(AppSettingsStore.DefaultPath));
        services.AddSingleton<WorkspaceState>();
        services.TryAddSingleton<IPlatformServices, BrowserPlatformServices>();

        // Per UI session (one per browser connection in the web host).
        services.AddScoped<DialogService>();
        services.AddScoped<UiActions>();
        return services;
    }
}
