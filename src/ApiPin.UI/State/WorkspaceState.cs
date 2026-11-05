using ApiPin.Core.Http;
using ApiPin.Core.Models;
using ApiPin.Storage;

namespace ApiPin.UI.State;

/// <summary>Open tabs and sending. Call <see cref="MarkDirty"/> after every edit; it refreshes the UI and saves shortly after.</summary>
public sealed class WorkspaceState
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);

    private readonly ScratchTabStore _store;
    private readonly IHttpExecutor _executor;
    private CancellationTokenSource? _pendingSave;

    public WorkspaceState(ScratchTabStore store, IHttpExecutor executor)
    {
        _store = store;
        _executor = executor;

        var saved = store.Load();
        Tabs.AddRange(saved.Tabs.Select(r => new RequestTab(r)));
        if (Tabs.Count == 0) Tabs.Add(new RequestTab(new RequestDefinition()));
        Active = Tabs.FirstOrDefault(t => t.Request.Id == saved.ActiveTabId) ?? Tabs[0];
    }

    public List<RequestTab> Tabs { get; } = [];
    public RequestTab Active { get; private set; }

    /// <summary>Raised after any change that should re-render the UI.</summary>
    public event Action? Changed;

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

    /// <summary>Saves immediately and synchronously; for app shutdown.</summary>
    public void SaveNow()
    {
        _pendingSave?.Cancel();
        var (tabs, activeId) = Snapshot();
        try
        {
            Task.Run(() => _store.SaveAsync(tabs, activeId)).GetAwaiter().GetResult();
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
            await _store.SaveAsync(tabs, activeId, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            // Superseded by a newer save, or the disk is unavailable; the next edit retries.
        }
    }

    private (List<RequestDefinition> Tabs, Guid ActiveId) Snapshot() =>
        (Tabs.Select(t => t.Request.Clone(newId: false)).ToList(), Active.Request.Id);
}
