using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
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

        // Match the page background so there is no white flash before the web view paints.
        Background = WindowsTheme.IsDark ? new SolidColorBrush(Color.FromRgb(0x1e, 0x1f, 0x22)) : Brushes.White;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _services.GetRequiredService<WorkspaceState>().SaveNow();
        base.OnClosing(e);
    }
}
