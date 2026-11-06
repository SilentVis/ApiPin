using System.Windows;
using System.Windows.Threading;
using ApiPin.Storage.Logging;
using ApiPin.UI;
using ApiPin.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ApiPin.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
#if DEBUG
            logging.SetMinimumLevel(LogLevel.Debug);
#endif
            logging.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultDirectory));
        });
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton<IPlatformServices, WpfPlatformServices>();
        // ApiPin.exe [workspace-folder] opens that workspace instead of the last one.
        services.AddApiPin(startupWorkspace: e.Args.FirstOrDefault());
        _services = services.BuildServiceProvider();

        _log = _services.GetRequiredService<ILoggerFactory>().CreateLogger("ApiPin.Desktop");
        _log.LogInformation("ApiPin {Version} starting", typeof(App).Assembly.GetName().Version);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _log.LogCritical(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) => _log.LogError(args.Exception, "Unobserved task exception");

        new MainWindow(_services).Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        _log?.LogCritical(e.Exception, "Unhandled exception on the UI thread");

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.LogInformation("ApiPin exiting");
        _services?.Dispose();
        base.OnExit(e);
    }
}
