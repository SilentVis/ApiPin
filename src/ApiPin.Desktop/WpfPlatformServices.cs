using System.IO;
using System.Windows;
using ApiPin.UI.Services;
using Microsoft.Win32;

namespace ApiPin.Desktop;

internal sealed class WpfPlatformServices : IPlatformServices
{
    public bool SupportsFileDialogs => true;
    public bool RunsInBrowser => false;

    public Task<string?> PickFileAsync() => OnUiThread(() =>
    {
        var dialog = new OpenFileDialog { Title = "Choose a file", CheckFileExists = true };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    });

    public Task<string?> SaveFileAsync(string suggestedFileName, byte[] content) => OnUiThread(() =>
    {
        var dialog = new SaveFileDialog { Title = "Save response body", FileName = suggestedFileName, AddExtension = true };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return null;

        File.WriteAllBytes(dialog.FileName, content);
        return dialog.FileName;
    });

    private static Task<string?> OnUiThread(Func<string?> action) =>
        Application.Current.Dispatcher.InvokeAsync(action).Task;
}
