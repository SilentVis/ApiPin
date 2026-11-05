using System.ComponentModel;
using System.Windows;
using ApiPin.UI.State;
using Microsoft.Extensions.DependencyInjection;

namespace ApiPin.Desktop;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        Resources.Add("services", services);
        InitializeComponent();
        WindowsTheme.Attach(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _services.GetRequiredService<WorkspaceState>().SaveNow();
        base.OnClosing(e);
    }
}
