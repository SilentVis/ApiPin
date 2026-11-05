using Microsoft.Win32;

namespace ApiPin.Desktop;

internal static class WindowsTheme
{
    /// <summary>True when Windows "app mode" is dark. The web UI follows it on its own via prefers-color-scheme.</summary>
    public static bool IsDark =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
}
