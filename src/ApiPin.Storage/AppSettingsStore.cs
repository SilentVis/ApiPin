using System.Text.Json;
using ApiPin.Core.Models;

namespace ApiPin.Storage;

public sealed record AppSettings
{
    /// <summary>Workspace folder opened last; reopened on start.</summary>
    public string? LastWorkspace { get; init; }

    /// <summary>Proxy for every outgoing request.</summary>
    public ProxySettings Proxy { get; init; } = new();

    /// <summary>Recently opened workspace folders, most recent first.</summary>
    public IReadOnlyList<string> RecentWorkspaces { get; init; } = [];

    public const int MaxRecent = 8;

    /// <summary>A copy with <paramref name="path"/> as the last workspace and at the top of the recent list.</summary>
    public AppSettings WithWorkspace(string path) => this with
    {
        LastWorkspace = path,
        RecentWorkspaces = RecentWorkspaces
            .Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
            .Prepend(path)
            .Take(MaxRecent)
            .ToList(),
    };

    public AppSettings WithoutRecent(string path) => this with
    {
        RecentWorkspaces = RecentWorkspaces.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList(),
    };
}

/// <summary>App-level settings in %APPDATA%\ApiPin\settings.json (never in a workspace).</summary>
public sealed class AppSettingsStore(string filePath)
{
    public static string DefaultPath =>
        AppDataPaths.File("settings.json");

    /// <summary>Default workspace for first start: Documents\ApiPin.</summary>
    public static string DefaultWorkspace =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ApiPin");

    // Read on every request (the proxy), so the parsed file is kept until it changes on disk.
    private (DateTime Stamp, AppSettings Settings)? _cached;
    private readonly Lock _lock = new();

    public AppSettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(filePath)) return new();
                var stamp = File.GetLastWriteTimeUtc(filePath);
                if (_cached is { } c && c.Stamp == stamp) return c.Settings;
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath), JsonDefaults.Options) ?? new();
                _cached = (stamp, settings);
                return settings;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return new();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(settings, JsonDefaults.Options));
            _cached = (File.GetLastWriteTimeUtc(filePath), settings);
        }
    }
}
