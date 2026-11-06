namespace ApiPin.Storage;

/// <summary>
/// Watches a workspace folder for changes made outside ApiPin (a git pull, another editor) and raises
/// <see cref="Changed"/> once things settle. Changes the <see cref="WorkspaceStore"/> made itself are ignored.
/// </summary>
public sealed class WorkspaceWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan OwnChangeWindow = TimeSpan.FromSeconds(3);

    private readonly WorkspaceStore _store;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;

    public WorkspaceWatcher(WorkspaceStore store)
    {
        _store = store;
        _timer = new Timer(_ => Changed?.Invoke());
        _watcher = new FileSystemWatcher(store.Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += OnEvent;
        _watcher.Created += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += (_, _) => Schedule(); // buffer overflow: some events were lost, so reload anyway
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Raised on a thread-pool thread after external changes.</summary>
    public event Action? Changed;

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (IsRelevant(e.FullPath)) Schedule();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsRelevant(e.FullPath) || IsRelevant(e.OldFullPath)) Schedule();
    }

    /// <summary>Workspace JSON files and folders, excluding temp files, .git, .apipin and our own writes.</summary>
    internal bool IsRelevant(string path)
    {
        var relative = Path.GetRelativePath(_store.Root, path);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (first is ".git" or ".apipin" or ".vs" or "node_modules") return false;
        if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;

        var extension = Path.GetExtension(path);
        if (extension.Length > 0 && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return false;

        return !_store.IsOwnChange(path, OwnChangeWindow);
    }

    private void Schedule() => _timer.Change(Debounce, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }
}
