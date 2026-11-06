using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace ApiPin.Storage;

/// <summary>Values of secret variables, kept outside the workspace folder so they never reach Git.</summary>
public interface ISecretStore
{
    string? Get(string key);

    /// <summary>Replaces every entry whose key starts with <paramref name="prefix"/> by <paramref name="values"/> (keys without the prefix).</summary>
    void ReplaceScope(string prefix, IReadOnlyDictionary<string, string> values);
}

/// <summary>For tests and platforms without DPAPI: secrets live only as long as the process.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public IReadOnlyDictionary<string, string> Snapshot
    {
        get { lock (_lock) return new Dictionary<string, string>(_values); }
    }

    public string? Get(string key)
    {
        lock (_lock) return _values.GetValueOrDefault(key);
    }

    public void ReplaceScope(string prefix, IReadOnlyDictionary<string, string> values)
    {
        lock (_lock) SecretScopes.Replace(_values, prefix, values);
    }
}

/// <summary>
/// Secrets encrypted with Windows DPAPI for the current user, in one file in app data (<c>secrets.dat</c>).
/// Only this Windows account on this machine can read them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string filePath) : ISecretStore
{
    private static readonly byte[] Entropy = "ApiPin.secrets.v1"u8.ToArray();

    private readonly Lock _lock = new();
    private Dictionary<string, string>? _values;

    public static string DefaultPath => AppDataPaths.File("secrets.dat");

    public string? Get(string key)
    {
        lock (_lock) return Values().GetValueOrDefault(key);
    }

    public void ReplaceScope(string prefix, IReadOnlyDictionary<string, string> values)
    {
        lock (_lock)
        {
            var all = Values();
            if (!SecretScopes.Replace(all, prefix, values)) return;
            var plain = JsonSerializer.SerializeToUtf8Bytes(all);
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            var temp = filePath + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, filePath, overwrite: true);
        }
    }

    private Dictionary<string, string> Values()
    {
        if (_values is not null) return _values;
        try
        {
            if (File.Exists(filePath))
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(filePath), Entropy, DataProtectionScope.CurrentUser);
                return _values = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Unreadable (another user's file, corruption): start empty; secrets show as missing.
        }
        return _values = [];
    }
}

internal static class SecretScopes
{
    /// <summary>Returns true when something changed.</summary>
    public static bool Replace(Dictionary<string, string> all, string prefix, IReadOnlyDictionary<string, string> values)
    {
        var changed = false;
        foreach (var key in all.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            if (values.ContainsKey(key[prefix.Length..])) continue;
            all.Remove(key);
            changed = true;
        }
        foreach (var (name, value) in values)
        {
            if (all.TryGetValue(prefix + name, out var existing) && existing == value) continue;
            all[prefix + name] = value;
            changed = true;
        }
        return changed;
    }

}
