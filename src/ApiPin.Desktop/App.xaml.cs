using System.Windows;
using ApiPin.UI;
using ApiPin.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPin.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton<IPlatformServices, WpfPlatformServices>();
        services.AddApiPin();
        _services = services.BuildServiceProvider();

        new MainWindow(_services).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
