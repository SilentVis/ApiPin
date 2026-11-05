using System.Text;
using ApiPin.Core.Models;
using ApiPin.UI.State;
using Microsoft.JSInterop;

namespace ApiPin.UI.Services;

/// <summary>User actions that need a dialog or a file on top of <see cref="WorkspaceState"/>.</summary>
public sealed class UiActions(WorkspaceState state, DialogService dialogs, IPlatformServices platform, IJSRuntime js)
{
    /// <summary>Ctrl+S: saves over the saved request, or asks where to save a new one.</summary>
    public Task SaveAsync(RequestTab tab) => state.Save(tab) ? Task.CompletedTask : SaveAsAsync(tab);

    public async Task SaveAsAsync(RequestTab tab)
    {
        if (state.Workspace is not { } workspace) return;

        var current = state.Find(tab.Request.Id);
        var name = string.IsNullOrWhiteSpace(tab.Request.Name) ? tab.Title : tab.Request.Name;
        if (await dialogs.SaveAsAsync(name, Targets(workspace.OrderedCollections), current?.Parent.Id) is not { } result) return;

        var target = result.FolderId is { } id
            ? workspace.Collections.SelectMany(c => c.SelfAndDescendants()).FirstOrDefault(f => f.Id == id)
            : state.CreateCollection(result.NewCollectionName ?? "New collection");
        if (target is not null) state.SaveAs(tab, target, result.Name);
    }

    /// <summary>Closes a tab, asking first when it has unsaved changes.</summary>
    public async Task CloseAsync(RequestTab tab)
    {
        if (tab.IsModified)
        {
            var discard = await dialogs.ConfirmAsync("Close tab",
                $"'{tab.Title}' has unsaved changes. Close it and lose them?", "Close without saving", danger: true);
            if (!discard) return;
        }
        state.Close(tab);
    }

    public async Task NewCollectionAsync()
    {
        if (await dialogs.PromptAsync("New collection", "Name", "", "Create") is { } name) state.CreateCollection(name);
    }

    public async Task<RequestFolder?> NewFolderAsync(RequestFolder parent) =>
        await dialogs.PromptAsync("New folder", "Name", "", "Create") is { } name ? state.CreateFolder(parent, name) : null;

    public async Task RenameAsync(RequestFolder folder)
    {
        var what = folder is RequestCollection ? "collection" : "folder";
        if (await dialogs.PromptAsync($"Rename {what}", "Name", folder.Name, "Rename") is { } name) state.Rename(folder, name);
    }

    public async Task RenameAsync(RequestDefinition request)
    {
        if (await dialogs.PromptAsync("Rename request", "Name", request.Name, "Rename") is { } name) state.Rename(request, name);
    }

    public async Task DeleteAsync(RequestFolder folder)
    {
        var count = folder.AllRequests().Count();
        var what = folder is RequestCollection ? "collection" : "folder";
        var detail = count == 0 ? "" : $" and the {count} request{(count == 1 ? "" : "s")} in it";
        if (await dialogs.ConfirmAsync($"Delete {what}", $"Delete '{folder.Name}'{detail}? The files are removed from the workspace folder.", "Delete", danger: true))
            state.Delete(folder);
    }

    public async Task DeleteAsync(RequestDefinition request)
    {
        if (await dialogs.ConfirmAsync("Delete request", $"Delete '{request.Name}'? Its file is removed from the workspace folder.", "Delete", danger: true))
            state.Delete(request);
    }

    public async Task ExportPostmanAsync(RequestCollection collection)
    {
        var (fileName, json) = WorkspaceState.ExportPostman(collection);
        await SaveFileAsync(fileName, "application/json", Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Opens another workspace folder: a folder dialog on desktop, a path prompt in the browser.</summary>
    public async Task OpenWorkspaceAsync()
    {
        var path = platform.SupportsFileDialogs
            ? await platform.PickFolderAsync()
            : await dialogs.PromptAsync("Open workspace", "Folder path (created if missing)", state.Workspace?.Root ?? "", "Open");
        if (path is not null) state.OpenWorkspace(path);
    }

    /// <summary>Native save dialog where there is one, a browser download otherwise. Returns the saved path, if known.</summary>
    public async Task<string?> SaveFileAsync(string fileName, string contentType, byte[] content)
    {
        if (platform.SupportsFileDialogs) return await platform.SaveFileAsync(fileName, content);

        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/ApiPin.UI/apipin.js");
        await module.InvokeVoidAsync("downloadFile", fileName, contentType, Convert.ToBase64String(content));
        return null;
    }

    private static List<SaveTarget> Targets(IEnumerable<RequestCollection> collections)
    {
        var targets = new List<SaveTarget>();
        foreach (var collection in collections) Add(collection, collection.Name, 0);
        return targets;

        void Add(RequestFolder folder, string label, int depth)
        {
            targets.Add(new SaveTarget(folder.Id, label, depth));
            foreach (var sub in folder.OrderedFolders) Add(sub, $"{label} / {sub.Name}", depth + 1);
        }
    }
}
