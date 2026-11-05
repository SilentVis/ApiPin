using System.Text.Json;

namespace ApiPin.Storage;

public sealed record AppSettings
{
    /// <summary>Workspace folder opened last; reopened on start.</summary>
    public string? LastWorkspace { get; init; }
}

/// <summary>App-level settings in %APPDATA%\ApiPin\settings.json (never in a workspace).</summary>
public sealed class AppSettingsStore(string filePath)
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ApiPin", "settings.json");

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
