using System.Text.Json;

namespace ApiPin.Storage;

public sealed record AppSettings
{
    /// <summary>Workspace folder opened last; reopened on start.</summary>
    public string? LastWorkspace { get; init; }

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

    public AppSettings Load()
    {
        try
        {
            return File.Exists(filePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath), JsonDefaults.Options) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(AppSettings settings) =>
        AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(settings, JsonDefaults.Options));
}
