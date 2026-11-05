using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ApiPin.Desktop;

/// <summary>
/// Makes the native title bar match the web UI's top bar. The page follows Windows' light/dark
/// setting by itself (prefers-color-scheme); this keeps the window chrome in step with it.
/// </summary>
internal static partial class WindowsTheme
{
    // Same values as --bg, --bg-2, --text and --border in ApiPin.UI/wwwroot/apipin.css.
    private static readonly Palette Light = new(Background: 0xffffff, Bar: 0xf5f6f8, Text: 0x1f2329, Border: 0xdcdfe5);
    private static readonly Palette Dark = new(Background: 0x1e1f22, Bar: 0x2b2d31, Text: 0xe3e5e8, Border: 0x3a3c42);

    private const int DwmUseImmersiveDarkMode = 20;  // Windows 10 20H1+
    private const int DwmBorderColor = 34;           // Windows 11+
    private const int DwmCaptionColor = 35;          // Windows 11+
    private const int DwmTextColor = 36;             // Windows 11+

    /// <summary>True when Windows "app mode" is dark.</summary>
    public static bool IsDark =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    /// <summary>Themes the window now and again whenever the Windows theme changes.</summary>
    public static void Attach(Window window)
    {
        void Apply() => ApplyTo(window);
        UserPreferenceChangedEventHandler onChange = (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                window.Dispatcher.BeginInvoke(Apply);
        };

        window.SourceInitialized += (_, _) => Apply();
        SystemEvents.UserPreferenceChanged += onChange;
        window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= onChange;
    }

    private static void ApplyTo(Window window)
    {
        var palette = IsDark ? Dark : Light;

        // The window background shows before the web view paints; match the page to avoid a flash.
        window.Background = new SolidColorBrush(ToColor(palette.Background));

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Older Windows versions ignore attributes they don't know; nothing to handle.
        SetInt(hwnd, DwmUseImmersiveDarkMode, IsDark ? 1 : 0);
        SetInt(hwnd, DwmCaptionColor, ToColorRef(palette.Bar));
        SetInt(hwnd, DwmTextColor, ToColorRef(palette.Text));
        SetInt(hwnd, DwmBorderColor, ToColorRef(palette.Border));
    }

    private static void SetInt(IntPtr hwnd, int attribute, int value) =>
        _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    /// <summary>0xRRGGBB to the COLORREF layout DWM expects (0x00BBGGRR).</summary>
    private static int ToColorRef(int rgb) => ((rgb & 0xff) << 16) | (rgb & 0xff00) | ((rgb >> 16) & 0xff);

    private static Color ToColor(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed record Palette(int Background, int Bar, int Text, int Border);
}
