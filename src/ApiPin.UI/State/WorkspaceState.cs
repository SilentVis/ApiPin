using System.Text.Json;
using ApiPin.Core.Http;
using ApiPin.Core.Models;
using ApiPin.Storage;
using ApiPin.Storage.Postman;
using Microsoft.Extensions.Logging;

namespace ApiPin.UI.State;

/// <summary>A workspace folder given at startup; it is opened instead of the last one and not remembered.</summary>
public sealed record WorkspaceStartup(string? Path);

/// <summary>Where a saved request lives in the open workspace.</summary>
public sealed record SavedLocation(RequestDefinition Request, RequestFolder Parent, RequestCollection Collection, IReadOnlyList<RequestFolder> Path);

/// <summary>
/// Open tabs, the open workspace (collections on disk) and sending.
/// Call <see cref="MarkDirty"/> after every edit; it refreshes the UI and saves the tabs shortly after.
/// </summary>
public sealed class WorkspaceState
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);

    private readonly ScratchTabStore _scratch;
    private readonly AppSettingsStore _settings;
    private readonly IHttpExecutor _executor;
    private readonly ILogger<WorkspaceState> _log;
    private CancellationTokenSource? _pendingSave;
    private WorkspaceStore? _store;

    public WorkspaceState(ScratchTabStore scratch, AppSettingsStore settings, IHttpExecutor executor, WorkspaceStartup startup, ILogger<WorkspaceState> log)
    {
        _log = log;
        _scratch = scratch;
        _settings = settings;
        _executor = executor;

        var saved = scratch.Load();
        Tabs.AddRange(saved.Tabs.Select(r => new RequestTab(r)));
        if (Tabs.Count == 0) Tabs.Add(new RequestTab(new RequestDefinition()));
        Active = Tabs.FirstOrDefault(t => t.Request.Id == saved.ActiveTabId) ?? Tabs[0];

        OpenWorkspace(startup.Path ?? settings.Load().LastWorkspace ?? AppSettingsStore.DefaultWorkspace, remember: false);
    }

    public List<RequestTab> Tabs { get; } = [];
    public RequestTab Active { get; private set; }

    /// <summary>The open workspace, or null when it could not be opened (see <see cref="Error"/>).</summary>
    public Workspace? Workspace { get; private set; }

    /// <summary>The last file-system error, shown until the next successful operation.</summary>
    public string? Error { get; private set; }

    /// <summary>Raised after any change that should re-render the UI.</summary>
    public event Action? Changed;

    // ---------- Tabs ----------

    public RequestTab NewTab()
    {
        var tab = new RequestTab(new RequestDefinition());
        Tabs.Insert(Tabs.IndexOf(Active) + 1, tab);
        Active = tab;
        MarkDirty();
        return tab;
    }

    public void Duplicate(RequestTab tab)
    {
        var copy = tab.Request.Clone();
        copy.Name = tab.Title + " (copy)";
        var newTab = new RequestTab(copy);
        Tabs.Insert(Tabs.IndexOf(tab) + 1, newTab);
        Active = newTab;
        MarkDirty();
    }

    public void Close(RequestTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;

        Cancel(tab);
        Tabs.RemoveAt(index);
        if (Tabs.Count == 0) Tabs.Add(new RequestTab(new RequestDefinition()));
        if (ReferenceEquals(Active, tab)) Active = Tabs[Math.Min(index, Tabs.Count - 1)];
        MarkDirty();
    }

    public void Activate(RequestTab tab)
    {
        if (ReferenceEquals(Active, tab) || !Tabs.Contains(tab)) return;
        Active = tab;
        MarkDirty();
    }

    public void ActivateRelative(int offset)
    {
        var index = (Tabs.IndexOf(Active) + offset + Tabs.Count) % Tabs.Count;
        Activate(Tabs[index]);
    }

    public void MarkDirty()
    {
        UpdateDirty(Active);
        Changed?.Invoke();
        ScheduleSave();
    }

    public async Task SendAsync(RequestTab tab)
    {
        if (tab.IsSending) return;

        using var cts = new CancellationTokenSource();
        tab.Sending = cts;
        tab.IsSending = true;
        Changed?.Invoke();
        try
        {
            // Send a snapshot, so edits made while waiting don't change the request in flight.
            tab.Response = await _executor.SendAsync(tab.Request.Clone(newId: false), cts.Token);
        }
        finally
        {
            tab.IsSending = false;
            tab.Sending = null;
            Changed?.Invoke();
        }
    }

    public void Cancel(RequestTab tab) => tab.Sending?.Cancel();

    // ---------- Workspace ----------

    /// <summary>Opens (or creates) a workspace folder and links open tabs to its saved requests.</summary>
    public bool OpenWorkspace(string path, bool remember = true)
    {
        try
        {
            var store = new WorkspaceStore(path);
            Workspace = store.Load();
            _store = store;
            Error = null;
            if (remember) _settings.Save(new AppSettings { LastWorkspace = store.Root });
            _log.LogInformation("Opened workspace {Root}: {Collections} collections, {Problems} skipped files",
                store.Root, Workspace.Collections.Count, Workspace.Problems.Count);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            _log.LogWarning(ex, "Could not open workspace {Path}", path);
            Error = $"Could not open workspace '{path}': {ex.Message}";
            if (_store is null) Workspace = null;
            Changed?.Invoke();
            return false;
        }

        foreach (var tab in Tabs) Relink(tab);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Re-reads the workspace from disk, e.g. after a git pull.</summary>
    public void Reload()
    {
        if (_store is not null) OpenWorkspace(_store.Root, remember: false);
    }

    public void DismissError()
    {
        Error = null;
        Changed?.Invoke();
    }

    public SavedLocation? Find(Guid requestId)
    {
        if (Workspace is null) return null;
        foreach (var collection in Workspace.Collections)
        {
            if (FindIn(collection, collection, [], requestId) is { } found) return found;
        }
        return null;
    }

    /// <summary>The folder that contains <paramref name="folder"/>, or null for a collection.</summary>
    public RequestFolder? ParentOf(RequestFolder folder) =>
        Workspace?.Collections.SelectMany(c => c.SelfAndDescendants()).FirstOrDefault(f => f.Folders.Contains(folder));

    public bool IsSaved(RequestTab tab) => tab.SavedJson is not null;

    /// <summary>Opens a saved request, or switches to the tab that already has it.</summary>
    public void Open(RequestDefinition saved)
    {
        if (Tabs.FirstOrDefault(t => t.Request.Id == saved.Id) is { } open)
        {
            Activate(open);
            return;
        }

        var tab = new RequestTab(saved.Clone(newId: false)) { SavedJson = Json(saved) };
        // Replace an untouched blank tab instead of piling up empty ones.
        var blank = IsBlank(Active) ? Active : null;
        Tabs.Insert(Tabs.IndexOf(Active) + 1, tab);
        if (blank is not null) Tabs.Remove(blank);
        Active = tab;
        MarkDirty();
    }

    /// <summary>Saves a tab over its saved request. Returns false when it isn't saved anywhere yet (use <see cref="SaveAs"/>).</summary>
    public bool Save(RequestTab tab)
    {
        if (Find(tab.Request.Id) is not { } location) return false;

        var copy = tab.Request.Clone(newId: false);
        copy.Seq = location.Request.Seq;
        Run(() =>
        {
            _store!.SaveRequest(location.Parent, copy);
            location.Parent.Requests[location.Parent.Requests.IndexOf(location.Request)] = copy;
            tab.Request.Seq = copy.Seq;
            tab.SavedJson = Json(copy);
        });
        return true;
    }

    /// <summary>Saves a tab as a new request in <paramref name="target"/>; the tab then points to the new request.</summary>
    public void SaveAs(RequestTab tab, RequestFolder target, string name)
    {
        var copy = tab.Request.Clone(newId: IsSaved(tab));
        copy.Name = name;
        copy.Seq = target.NextSeq;
        Run(() =>
        {
            _store!.SaveRequest(target, copy);
            target.Requests.Add(copy);
            tab.Request.Id = copy.Id;
            tab.Request.Name = copy.Name;
            tab.Request.Seq = copy.Seq;
            tab.SavedJson = Json(copy);
        });
    }

    public RequestCollection? CreateCollection(string name)
    {
        if (Workspace is null) return null;
        var collection = new RequestCollection { Name = name, Seq = Workspace.Collections.Select(c => c.Seq).DefaultIfEmpty(-1).Max() + 1 };
        return Run(() =>
        {
            _store!.SaveCollection(collection);
            Workspace.Collections.Add(collection);
        }) ? collection : null;
    }

    public RequestFolder? CreateFolder(RequestFolder parent, string name)
    {
        var folder = new RequestFolder { Name = name, Seq = parent.NextSeq };
        return Run(() =>
        {
            _store!.SaveFolder(parent, folder);
            parent.Folders.Add(folder);
        }) ? folder : null;
    }

    /// <summary>Creates an empty saved request in <paramref name="parent"/> and opens it.</summary>
    public void CreateRequest(RequestFolder parent)
    {
        var request = new RequestDefinition { Name = "New request", Seq = parent.NextSeq };
        if (Run(() =>
            {
                _store!.SaveRequest(parent, request);
                parent.Requests.Add(request);
            }))
        {
            Open(request);
        }
    }

    public void Rename(RequestFolder folder, string name)
    {
        var previous = folder.Name;
        folder.Name = name;
        var ok = Run(() =>
        {
            if (folder is RequestCollection collection) _store!.SaveCollection(collection);
            else _store!.SaveFolder(ParentOf(folder)!, folder);
        });
        if (!ok) folder.Name = previous;
    }

    public void Rename(RequestDefinition saved, string name)
    {
        if (Find(saved.Id) is not { } location) return;
        var previous = saved.Name;
        saved.Name = name;
        var ok = Run(() =>
        {
            _store!.SaveRequest(location.Parent, saved);
            if (Tabs.FirstOrDefault(t => t.Request.Id == saved.Id) is { } tab)
            {
                tab.Request.Name = name;
                tab.SavedJson = Json(saved);
                UpdateDirty(tab);
            }
        });
        if (!ok) saved.Name = previous;
    }

    public void DuplicateSaved(RequestDefinition saved)
    {
        if (Find(saved.Id) is not { } location) return;
        var copy = saved.Clone();
        copy.Name = saved.Name + " (copy)";
        copy.Seq = location.Parent.NextSeq;
        if (Run(() =>
            {
                _store!.SaveRequest(location.Parent, copy);
                location.Parent.Requests.Add(copy);
            }))
        {
            Open(copy);
        }
    }

    public void Delete(RequestDefinition saved)
    {
        if (Find(saved.Id) is not { } location) return;
        Run(() =>
        {
            _store!.Delete(saved.Id);
            location.Parent.Requests.Remove(saved);
        });
        foreach (var tab in Tabs) Relink(tab);
    }

    public void Delete(RequestFolder folder)
    {
        Run(() =>
        {
            _store!.Delete(folder.Id);
            if (folder is RequestCollection collection) Workspace!.Collections.Remove(collection);
            else ParentOf(folder)?.Folders.Remove(folder);
        });
        foreach (var tab in Tabs) Relink(tab);
    }

    public static (string FileName, string Json) ExportPostman(RequestCollection collection) =>
        (PostmanExporter.FileName(collection), PostmanExporter.Export(collection));

    // ---------- Persistence of open tabs ----------

    /// <summary>Saves immediately and synchronously; for app shutdown.</summary>
    public void SaveNow()
    {
        _pendingSave?.Cancel();
        var (tabs, activeId) = Snapshot();
        try
        {
            Task.Run(() => _scratch.SaveAsync(tabs, activeId)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do while shutting down.
        }
    }

    private async void ScheduleSave()
    {
        _pendingSave?.Cancel();
        var cts = _pendingSave = new CancellationTokenSource();
        try
        {
            await Task.Delay(SaveDelay, cts.Token);
            var (tabs, activeId) = Snapshot();
            await _scratch.SaveAsync(tabs, activeId, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            // Superseded by a newer save, or the disk is unavailable; the next edit retries.
        }
    }

    private (List<RequestDefinition> Tabs, Guid ActiveId) Snapshot() =>
        (Tabs.Select(t => t.Request.Clone(newId: false)).ToList(), Active.Request.Id);

    // ---------- Helpers ----------

    private void Relink(RequestTab tab)
    {
        tab.SavedJson = Find(tab.Request.Id) is { } location ? Json(location.Request) : null;
        UpdateDirty(tab);
    }

    private static void UpdateDirty(RequestTab tab) =>
        tab.IsModified = tab.SavedJson is not null && Json(tab.Request) != tab.SavedJson;

    private static string Json(RequestDefinition request) => JsonSerializer.Serialize(request, JsonDefaults.Options);

    private static bool IsBlank(RequestTab tab) =>
        tab.SavedJson is null && tab.Response is null && !tab.IsSending && Json(tab.Request) == Json(new RequestDefinition { Id = tab.Request.Id });

    /// <summary>Runs a file-system change; on failure keeps the message in <see cref="Error"/> instead of throwing.</summary>
    private bool Run(Action action)
    {
        if (_store is null || Workspace is null)
        {
            Error = "No workspace is open.";
            Changed?.Invoke();
            return false;
        }
        try
        {
            action();
            Error = null;
            return true;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            _log.LogWarning(ex, "Workspace change failed");
            Error = ex.Message;
            return false;
        }
        finally
        {
            UpdateDirty(Active);
            Changed?.Invoke();
            ScheduleSave();
        }
    }

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static SavedLocation? FindIn(RequestFolder folder, RequestCollection collection, List<RequestFolder> path, Guid id)
    {
        if (folder.Requests.FirstOrDefault(r => r.Id == id) is { } request)
            return new SavedLocation(request, folder, collection, [.. path, folder]);

        foreach (var sub in folder.Folders)
        {
            if (FindIn(sub, collection, [.. path, folder], id) is { } found) return found;
        }
        return null;
    }
}
