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
    public static IServiceCollection AddApiPin(this IServiceCollection services, string? scratchFilePath = null)
    {
        services.AddSingleton<HandlerPool>();
        services.AddSingleton(new HttpExecutorOptions());
        services.AddSingleton<IHttpExecutor, HttpExecutor>();
        services.AddSingleton(new ScratchTabStore(scratchFilePath ?? ScratchTabStore.DefaultPath));
        services.AddSingleton<WorkspaceState>();
        services.TryAddSingleton<IPlatformServices, BrowserPlatformServices>();
        return services;
    }
}
