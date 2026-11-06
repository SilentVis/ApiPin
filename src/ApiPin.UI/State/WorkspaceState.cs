using System.Text.Json;
using ApiPin.Core.Execution;
using ApiPin.Core.Http;
using ApiPin.Core.Models;
using ApiPin.Core.Variables;
using ApiPin.Storage;
using ApiPin.Storage.Postman;
using Microsoft.Extensions.Logging;

namespace ApiPin.UI.State;

/// <summary>A workspace folder given at startup; it is opened instead of the last one and not remembered.</summary>
public sealed record WorkspaceStartup(string? Path);

/// <summary>Where a saved request lives in the open workspace. <see cref="Path"/> runs from the collection to the parent folder.</summary>
public sealed record SavedLocation(RequestDefinition Request, RequestFolder Parent, RequestCollection Collection, IReadOnlyList<RequestFolder> Path);

/// <summary>
/// Open tabs, the open workspace (collections on disk) and sending.
/// Call <see cref="MarkDirty"/> after every edit; it refreshes the UI and saves the tabs shortly after.
/// </summary>
public sealed class WorkspaceState : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);

    private readonly ScratchTabStore _scratch;
    private readonly AppSettingsStore _settings;
    private readonly IHttpExecutor _executor;
    private readonly ISecretStore _secrets;
    private readonly ILogger<WorkspaceState> _log;
    private CancellationTokenSource? _pendingSave;
    private WorkspaceStore? _store;
    private WorkspaceWatcher? _watcher;
    private volatile bool _externalChangePending;

    public WorkspaceState(ScratchTabStore scratch, AppSettingsStore settings, IHttpExecutor executor, ISecretStore secrets, HistoryStore history,
        WorkspaceStartup startup, ILogger<WorkspaceState> log)
    {
        History = history;
        _log = log;
        _secrets = secrets;
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

    /// <summary>
    /// Raised on a background thread when workspace files changed outside ApiPin. The UI calls
    /// <see cref="ApplyExternalChanges"/> on its own thread in response.
    /// </summary>
    public event Action? ExternalChange;

    /// <summary>Recently opened workspace folders other than the open one.</summary>
    public IReadOnlyList<string> RecentWorkspaces =>
        _settings.Load().RecentWorkspaces.Where(p => !string.Equals(p, _store?.Root, StringComparison.OrdinalIgnoreCase)).ToList();

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
        if (!IsBlank(tab))
        {
            _closed.Push(tab.Request.Clone(newId: false));
            if (_closed.Count > MaxClosed) _closed = new Stack<RequestDefinition>(_closed.Take(MaxClosed).Reverse());
        }
        if (Tabs.Count == 0) Tabs.Add(new RequestTab(new RequestDefinition()));
        if (ReferenceEquals(Active, tab)) Active = Tabs[Math.Min(index, Tabs.Count - 1)];
        MarkDirty();
    }

    private const int MaxClosed = 20;
    private Stack<RequestDefinition> _closed = new();

    public bool CanReopen => _closed.Count > 0;

    /// <summary>Ctrl+Shift+T: brings back the most recently closed tab (linked to its saved request again if there is one).</summary>
    public void ReopenClosed()
    {
        if (!_closed.TryPop(out var request)) return;
        if (Tabs.FirstOrDefault(t => t.Request.Id == request.Id) is { } open)
        {
            Activate(open);
            return;
        }
        var tab = new RequestTab(request);
        Tabs.Insert(Tabs.IndexOf(Active) + 1, tab);
        Active = tab;
        Relink(tab);
        MarkDirty();
    }

    /// <summary>The tabs other than <paramref name="keep"/>, for "Close others".</summary>
    public IReadOnlyList<RequestTab> OthersThan(RequestTab keep) => Tabs.Where(t => !ReferenceEquals(t, keep)).ToList();

    /// <summary>The tabs after <paramref name="tab"/>, for "Close tabs to the right".</summary>
    public IReadOnlyList<RequestTab> RightOf(RequestTab tab) => Tabs.Skip(Tabs.IndexOf(tab) + 1).ToList();

    /// <summary>Drag and drop in the tab strip.</summary>
    public void MoveTab(RequestTab tab, int index)
    {
        if (!Tabs.Remove(tab)) return;
        Tabs.Insert(Math.Clamp(index, 0, Tabs.Count), tab);
        MarkDirty();
    }

    /// <summary>Opens a request in a new tab, e.g. from history or an import.</summary>
    public RequestTab OpenNew(RequestDefinition request, ResponseResult? response = null)
    {
        var tab = new RequestTab(request) { Response = response };
        var blank = IsBlank(Active) ? Active : null;
        Tabs.Insert(Tabs.IndexOf(Active) + 1, tab);
        if (blank is not null) Tabs.Remove(blank);
        Active = tab;
        MarkDirty();
        return tab;
    }

    /// <summary>A pasted curl command replaces the tab's request (keeping its id, name and saved location).</summary>
    public void ApplyCurl(RequestTab tab, RequestDefinition parsed)
    {
        parsed.Id = tab.Request.Id;
        if (tab.Request.Name.Length > 0) parsed.Name = tab.Request.Name;
        parsed.Seq = tab.Request.Seq;
        tab.Request = parsed;
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

    // ---------- Sending ----------

    /// <summary>The folders a tab's request inherits from: collection first, down to its folder. Empty for an unsaved tab.</summary>
    public IReadOnlyList<RequestFolder> ChainFor(RequestTab tab) => Find(tab.Request.Id)?.Path ?? [];

    /// <summary>The variables in effect for a tab (folder and collection variables included).</summary>
    public VariableContext VariablesFor(RequestTab tab) => InheritanceResolver.WithFolderVariables(BaseVariables(), ChainFor(tab));

    /// <summary>The request as it will be sent: inherited auth and headers applied, variables resolved.</summary>
    public PreparedRequest Prepare(RequestTab tab) => RequestPreparer.Prepare(tab.Request, ChainFor(tab), BaseVariables());

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
            var prepared = Prepare(tab);
            tab.LastReport = prepared.Report;
            var snapshot = tab.Request.Clone(newId: false);
            tab.Response = await _executor.SendAsync(prepared.Request, CookieJar, cts.Token);
            RecordHistory(snapshot, tab.Response);
        }
        finally
        {
            tab.IsSending = false;
            tab.Sending = null;
            Changed?.Invoke();
        }
    }

    public void Cancel(RequestTab tab) => tab.Sending?.Cancel();

    // ---------- History and cookies ----------

    public HistoryStore History { get; }

    private void RecordHistory(RequestDefinition request, ResponseResult response)
    {
        if (response.Error?.Kind == ErrorKind.Cancelled) return;
        try
        {
            var settings = _settings.Load().History;
            var saved = Find(request.Id) is not null ? request.Id : (Guid?)null;
            History.Add(HistoryEntry.From(request, response, settings, Workspace?.Root, saved, ActiveEnvironment?.Name), settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Could not record history");
        }
    }

    /// <summary>Opens a history entry in a new tab, with its response when the body was kept.</summary>
    public void Restore(HistoryEntry entry)
    {
        var request = entry.Request.Clone(newId: true);
        // Back to the saved request when it still exists and isn't open; otherwise an unsaved copy.
        if (entry.SavedRequestId is { } id && Find(id) is not null && Tabs.All(t => t.Request.Id != id)) request.Id = id;
        var tab = OpenNew(request, entry.ToResponse());
        Relink(tab);
    }

    private readonly Dictionary<Guid, System.Net.CookieContainer> _jars = [];

    /// <summary>The cookie jar of the active environment (one for "No environment" too). Kept until the app closes.</summary>
    public System.Net.CookieContainer CookieJar
    {
        get
        {
            var key = ActiveEnvironment?.Id ?? Guid.Empty;
            if (!_jars.TryGetValue(key, out var jar)) _jars[key] = jar = new System.Net.CookieContainer();
            return jar;
        }
    }

    public void ClearCookieJar()
    {
        _jars.Remove(ActiveEnvironment?.Id ?? Guid.Empty);
        Changed?.Invoke();
    }

    // ---------- Import ----------

    /// <summary>Saves an imported collection into the workspace.</summary>
    public RequestCollection? ImportCollection(RequestCollection imported)
    {
        if (Workspace is null) return null;
        var ws = Workspace;
        imported.Seq = ws.Collections.Select(c => c.Seq).DefaultIfEmpty(-1).Max() + 1;
        var ok = Run(() =>
        {
            _store!.SaveCollection(imported);
            SaveTree(imported);
            ws.Collections.Add(imported);
        });
        if (!ok) Reload();
        return ok ? imported : null;

        void SaveTree(RequestFolder folder)
        {
            foreach (var request in folder.Requests) _store!.SaveRequest(folder, request);
            foreach (var sub in folder.Folders)
            {
                _store!.SaveFolder(folder, sub);
                SaveTree(sub);
            }
        }
    }

    public EnvironmentDefinition? ImportEnvironment(EnvironmentDefinition environment)
    {
        if (Workspace is null) return null;
        var ws = Workspace;
        var name = environment.Name;
        for (var i = 2; ws.Environments.Any(e => e.Name.Equals(environment.Name, StringComparison.OrdinalIgnoreCase)); i++) environment.Name = $"{name} {i}";
        return Run(() =>
        {
            _store!.SaveEnvironment(environment);
            ws.Environments.Add(environment);
        }) ? environment : null;
    }

    /// <summary>Runtime, environment and global variables. Collection and folder variables are added per request.</summary>
    private VariableContext BaseVariables()
    {
        var context = VariableContext.Empty;
        if (Workspace is null) return context;
        if (Workspace.ActiveEnvironment is { } env) context = context.With(VariableSource.Environment, env.Name, env.Variables);
        return context.With(VariableSource.Global, "Globals", Workspace.Globals);
    }

    // ---------- Environments ----------

    public EnvironmentDefinition? ActiveEnvironment => Workspace?.ActiveEnvironment;

    /// <summary>Picks the environment every request resolves its variables against; null for none.</summary>
    public void SetActiveEnvironment(Guid? id)
    {
        if (Workspace is null || Workspace.ActiveEnvironmentId == id) return;
        Workspace.ActiveEnvironmentId = Workspace.Environments.Any(e => e.Id == id) ? id : null;
        Run(() => _store!.SaveActiveEnvironment(Workspace.ActiveEnvironmentId));
    }

    /// <summary>
    /// Applies the environment manager's edits: saves new and changed environments, deletes removed ones,
    /// and saves the globals. Secret values go to the secret store, not the files.
    /// </summary>
    public void SaveEnvironments(IReadOnlyList<EnvironmentDefinition> environments, List<Variable> globals)
    {
        if (Workspace is null) return;
        var ws = Workspace;
        var ok = Run(() =>
        {
            foreach (var removed in ws.Environments.Where(e => environments.All(n => n.Id != e.Id)).ToList())
            {
                _store!.Delete(removed.Id);
                ws.Environments.Remove(removed);
            }
            foreach (var env in environments)
            {
                var clean = env.Clone();
                clean.Variables = clean.Variables.Where(v => !v.IsEmpty).ToList();
                var existing = ws.Environments.FirstOrDefault(e => e.Id == env.Id);
                if (existing is not null && JsonSerializer.Serialize(existing) == JsonSerializer.Serialize(clean)) continue;
                _store!.SaveEnvironment(clean);
                if (existing is null) ws.Environments.Add(clean);
                else ws.Environments[ws.Environments.IndexOf(existing)] = clean;
            }

            var cleanGlobals = globals.Where(v => !v.IsEmpty).Select(v => v.Clone()).ToList();
            if (JsonSerializer.Serialize(cleanGlobals) != JsonSerializer.Serialize(ws.Globals))
            {
                ws.Globals = cleanGlobals;
                _store!.SaveGlobals(ws);
            }
            if (ws.ActiveEnvironmentId is { } active && ws.Environments.All(e => e.Id != active))
            {
                ws.ActiveEnvironmentId = null;
                _store!.SaveActiveEnvironment(null);
            }
        });
        if (!ok) Reload();
    }

    // ---------- Workspace ----------

    /// <summary>Opens (or creates) a workspace folder and links open tabs to its saved requests.</summary>
    public bool OpenWorkspace(string path, bool remember = true)
    {
        try
        {
            var store = new WorkspaceStore(path, _secrets);
            Workspace = store.Load();
            Watch(store); // a new store per load, and the watcher asks it which changes were its own
            _store = store;
            Error = null;
            if (remember) _settings.Save(_settings.Load().WithWorkspace(store.Root));
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

    public void ForgetRecent(string path)
    {
        _settings.Save(_settings.Load().WithoutRecent(path));
        Changed?.Invoke();
    }

    /// <summary>Re-reads the workspace from disk, e.g. after a git pull.</summary>
    public void Reload()
    {
        if (_store is not null) OpenWorkspace(_store.Root, remember: false);
    }

    /// <summary>Reloads after <see cref="ExternalChange"/>; does nothing if another UI session already did.</summary>
    public void ApplyExternalChanges()
    {
        if (!_externalChangePending) return;
        _externalChangePending = false;
        _log.LogInformation("Workspace files changed on disk; reloading");
        Reload();
    }

    private void Watch(WorkspaceStore store)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _watcher = new WorkspaceWatcher(store);
            _watcher.Changed += () =>
            {
                _externalChangePending = true;
                ExternalChange?.Invoke();
            };
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _log.LogWarning(ex, "Can't watch {Root} for changes; use Reload from disk after a git pull", store.Root);
        }
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

    /// <summary>The path from the collection down to <paramref name="folder"/> itself.</summary>
    public IReadOnlyList<RequestFolder> ChainOf(RequestFolder folder)
    {
        var chain = new List<RequestFolder> { folder };
        for (var parent = ParentOf(folder); parent is not null; parent = ParentOf(parent)) chain.Insert(0, parent);
        return chain;
    }

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
            tab.HasConflict = false;
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
            tab.HasConflict = false;
        });
    }

    /// <summary>Replaces a conflicted tab's edits with the version on disk.</summary>
    public void UseVersionOnDisk(RequestTab tab)
    {
        if (Find(tab.Request.Id) is { } location) tab.Request = location.Request.Clone(newId: false);
        tab.HasConflict = false;
        MarkDirty();
    }

    /// <summary>Keeps a conflicted tab's edits; the next save overwrites the file.</summary>
    public void KeepMyVersion(RequestTab tab)
    {
        tab.HasConflict = false;
        MarkDirty();
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
        if (!Run(() => SaveFolderOrCollection(folder))) folder.Name = previous;
    }

    /// <summary>Saves headers, auth, variables and description of a folder or collection.</summary>
    public void UpdateSettings(RequestFolder folder, FolderSettings settings)
    {
        var previous = folder.GetSettings();
        folder.ApplySettings(settings);
        if (!Run(() => SaveFolderOrCollection(folder))) folder.ApplySettings(previous);
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

    // ---------- Moving and reordering ----------

    /// <summary>Moves a saved request into <paramref name="target"/> at <paramref name="index"/> among its requests.</summary>
    public void Move(RequestDefinition saved, RequestFolder target, int index)
    {
        if (Find(saved.Id) is not { } location) return;
        var ok = Run(() =>
        {
            var ordered = target.OrderedRequests.Where(r => !ReferenceEquals(r, saved)).ToList();
            ordered.Insert(Math.Clamp(index, 0, ordered.Count), saved);
            location.Parent.Requests.Remove(saved);
            target.Requests.Add(saved);

            for (var i = 0; i < ordered.Count; i++)
            {
                var moved = ReferenceEquals(ordered[i], saved) && !ReferenceEquals(location.Parent, target);
                if (ordered[i].Seq == i && !moved) continue;
                ordered[i].Seq = i;
                _store!.SaveRequest(target, ordered[i]);
            }
        });
        if (!ok) Reload();
    }

    /// <summary>True when <paramref name="folder"/> may move into <paramref name="target"/> (not into itself or below itself).</summary>
    public static bool CanMove(RequestFolder folder, RequestFolder target) =>
        folder is not RequestCollection && !folder.SelfAndDescendants().Contains(target);

    /// <summary>Moves a folder into <paramref name="target"/> at <paramref name="index"/> among its folders.</summary>
    public void Move(RequestFolder folder, RequestFolder target, int index)
    {
        if (!CanMove(folder, target) || ParentOf(folder) is not { } parent) return;
        var ok = Run(() =>
        {
            var ordered = target.OrderedFolders.Where(f => !ReferenceEquals(f, folder)).ToList();
            ordered.Insert(Math.Clamp(index, 0, ordered.Count), folder);
            parent.Folders.Remove(folder);
            target.Folders.Add(folder);

            for (var i = 0; i < ordered.Count; i++)
            {
                var moved = ReferenceEquals(ordered[i], folder) && !ReferenceEquals(parent, target);
                if (ordered[i].Seq == i && !moved) continue;
                ordered[i].Seq = i;
                _store!.SaveFolder(target, ordered[i]);
            }
        });
        if (!ok) Reload();
    }

    /// <summary>Moves a collection to <paramref name="index"/> in the sidebar.</summary>
    public void Move(RequestCollection collection, int index)
    {
        if (Workspace is null) return;
        var ok = Run(() =>
        {
            var ordered = Workspace.OrderedCollections.Where(c => !ReferenceEquals(c, collection)).ToList();
            ordered.Insert(Math.Clamp(index, 0, ordered.Count), collection);
            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].Seq == i) continue;
                ordered[i].Seq = i;
                _store!.SaveCollection(ordered[i]);
            }
        });
        if (!ok) Reload();
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

    public void Dispose()
    {
        _watcher?.Dispose();
        _pendingSave?.Cancel();
    }

    // ---------- Helpers ----------

    /// <summary>
    /// Re-links a tab to its saved request after the workspace was (re)loaded. When the file changed on disk,
    /// an unedited tab picks up the new version and an edited one is flagged as a conflict.
    /// </summary>
    private void Relink(RequestTab tab)
    {
        var location = Find(tab.Request.Id);
        var onDisk = location is null ? null : Json(location.Request);
        if (location is not null && tab.SavedJson is not null && onDisk != tab.SavedJson)
        {
            if (tab.IsModified) tab.HasConflict = true;
            else tab.Request = location.Request.Clone(newId: false);
        }
        if (location is null) tab.HasConflict = false;
        tab.SavedJson = onDisk;
        UpdateDirty(tab);
    }

    private void SaveFolderOrCollection(RequestFolder folder)
    {
        if (folder is RequestCollection collection) _store!.SaveCollection(collection);
        else _store!.SaveFolder(ParentOf(folder)!, folder);
    }

    private static void UpdateDirty(RequestTab tab) =>
        tab.IsModified = tab.SavedJson is not null && Json(tab.Request) != tab.SavedJson;

    /// <summary>JSON for change detection. Seq is left out: reordering in the sidebar isn't an edit of the tab.</summary>
    private static string Json(RequestDefinition request)
    {
        var copy = request.Clone(newId: false);
        copy.Seq = 0;
        return JsonSerializer.Serialize(copy, JsonDefaults.Options);
    }

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
