namespace ApiPin.Storage;

internal static class AtomicFile
{
    /// <summary>Writes to a temp file next to the target, then swaps it in, so a crash never leaves a half-written file.</summary>
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, contents, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
