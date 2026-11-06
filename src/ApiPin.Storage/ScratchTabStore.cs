using System.Text.Json;
using ApiPin.Core.Models;

namespace ApiPin.Storage;

public sealed record ScratchTabs(IReadOnlyList<RequestDefinition> Tabs, Guid? ActiveTabId);

/// <summary>Keeps unsaved request tabs across restarts, in a single JSON file in app data.</summary>
public sealed class ScratchTabStore(string filePath)
{
    private const int CurrentVersion = 1;

    public string FilePath { get; } = filePath;

    public static string DefaultPath =>
        AppDataPaths.File("scratch-tabs.json");

    /// <summary>Returns no tabs when the file is missing or unreadable; never throws for bad content.</summary>
    public ScratchTabs Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new([], null);
            var file = JsonSerializer.Deserialize<ScratchFile>(File.ReadAllText(FilePath), JsonDefaults.Options);
            return new(file?.Tabs ?? [], file?.ActiveTabId);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new([], null);
        }
    }

    public Task SaveAsync(IEnumerable<RequestDefinition> tabs, Guid? activeTabId, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new ScratchFile(CurrentVersion, activeTabId, tabs.ToList()), JsonDefaults.Options);
        return AtomicFile.WriteAllTextAsync(FilePath, json, ct);
    }

    private sealed record ScratchFile(int Version, Guid? ActiveTabId, List<RequestDefinition> Tabs);
}
