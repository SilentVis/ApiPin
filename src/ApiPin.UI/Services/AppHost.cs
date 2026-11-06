using System.Diagnostics;
using System.Reflection;
using ApiPin.Storage;
using ApiPin.Storage.Logging;

namespace ApiPin.UI.Services;

/// <summary>Facts about the running host (version, folders) and, where supported, app updates.</summary>
public interface IAppHost
{
    /// <summary>"Desktop" or "Browser".</summary>
    string HostName { get; }

    /// <summary>The engine shown in About, e.g. the WebView2 version.</summary>
    string? WebViewVersion { get; }

    string LogsFolder { get; }

    /// <summary>Opens a folder in Explorer (on the machine running ApiPin).</summary>
    void OpenFolder(string path);

    /// <summary>True for an installed desktop app that can update itself.</summary>
    bool SupportsUpdates { get; }

    /// <summary>A newer version's number, already downloaded and ready to apply, or null.</summary>
    Task<string?> CheckAndDownloadUpdateAsync(Action<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Restarts into the downloaded update.</summary>
    void RestartToUpdate();
}

/// <summary>Version strings for About and the logs.</summary>
public static class AppVersion
{
    /// <summary>e.g. "0.6.0", or "0.6.0-beta.1"; without the "+commit" suffix the SDK adds.</summary>
    public static string Display
    {
        get
        {
            var informational = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "?";
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }
    }

    /// <summary>The commit the build came from, when the SDK recorded it.</summary>
    public static string? Commit
    {
        get
        {
            var informational = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[(plus + 1)..][..Math.Min(10, informational.Length - plus - 1)] : null;
        }
    }
}

/// <summary>Default host: no self-update (the browser version is run from source or a folder).</summary>
public class BasicAppHost : IAppHost
{
    public virtual string HostName => "Browser";
    public virtual string? WebViewVersion => null;
    public string LogsFolder => FileLoggerProvider.DefaultDirectory;

    public void OpenFolder(string path)
    {
        if (OperatingSystem.IsBrowser()) return; // the UI library also targets WebAssembly, which can't start processes
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false })?.Dispose();
    }

    public virtual bool SupportsUpdates => false;
    public virtual Task<string?> CheckAndDownloadUpdateAsync(Action<int>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    public virtual void RestartToUpdate() { }

    public static string DataFolder => AppDataPaths.Root;
}
