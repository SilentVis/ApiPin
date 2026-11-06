using ApiPin.Core.Execution;
using ApiPin.Core.Models;

namespace ApiPin.UI.Services;

public enum DialogKind { Prompt, Confirm, SaveAs, FolderSettings }

/// <summary>A folder offered as a save target, with its full path for display.</summary>
public sealed record SaveTarget(Guid FolderId, string Label, int Depth);

/// <summary>Result of the Save As dialog: either an existing folder or a new collection's name.</summary>
public sealed record SaveAsResult(string Name, Guid? FolderId, string? NewCollectionName);

public sealed class DialogRequest
{
    public required DialogKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Message { get; init; }
    public string? Label { get; init; }
    public string Value { get; set; } = "";
    public string OkLabel { get; init; } = "OK";
    public bool Danger { get; init; }

    // Save As
    public IReadOnlyList<SaveTarget> Targets { get; init; } = [];
    public Guid? TargetId { get; set; }
    public string NewCollectionName { get; set; } = "";

    // Folder settings
    public FolderSettings? Settings { get; init; }
    public bool IsCollection { get; init; }
    public InheritedAuth? ParentAuth { get; init; }
    public string Section { get; set; } = "headers";
    public Dictionary<Type, AuthConfig> AuthCache { get; } = [];

    /// <summary>Wide dialogs with multi-line fields, where Enter must not submit.</summary>
    public bool IsWide => Kind == DialogKind.FolderSettings;

    internal TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Modal dialogs as awaitable calls; <c>DialogHost</c> renders the current one.</summary>
public sealed class DialogService
{
    public DialogRequest? Current { get; private set; }
    public event Action? Changed;

    /// <summary>Returns the trimmed text, or null when cancelled or left empty.</summary>
    public async Task<string?> PromptAsync(string title, string label, string value = "", string okLabel = "OK")
    {
        var result = await Show(new DialogRequest { Kind = DialogKind.Prompt, Title = title, Label = label, Value = value, OkLabel = okLabel });
        return result is string text && text.Trim().Length > 0 ? text.Trim() : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string okLabel = "OK", bool danger = false) =>
        await Show(new DialogRequest { Kind = DialogKind.Confirm, Title = title, Message = message, OkLabel = okLabel, Danger = danger }) is true;

    public async Task<SaveAsResult?> SaveAsAsync(string name, IReadOnlyList<SaveTarget> targets, Guid? selected) =>
        await Show(new DialogRequest
        {
            Kind = DialogKind.SaveAs,
            Title = "Save request",
            Value = name,
            Targets = targets,
            TargetId = selected ?? targets.FirstOrDefault()?.FolderId,
            OkLabel = "Save",
        }) as SaveAsResult;

    /// <summary>Edits a copy of a folder's or collection's settings; returns it on Save, null on Cancel.</summary>
    public async Task<FolderSettings?> FolderSettingsAsync(RequestFolder folder, InheritedAuth? parentAuth)
    {
        var request = new DialogRequest
        {
            Kind = DialogKind.FolderSettings,
            Title = $"{(folder is RequestCollection ? "Collection" : "Folder")} settings: {folder.Name}",
            Settings = folder.GetSettings(),
            IsCollection = folder is RequestCollection,
            ParentAuth = parentAuth,
            OkLabel = "Save",
        };
        return await Show(request) is true ? request.Settings : null;
    }

    public void Complete(object? result)
    {
        if (Current is not { } dialog) return;
        Current = null;
        Changed?.Invoke();
        dialog.Completion.TrySetResult(result);
    }

    private Task<object?> Show(DialogRequest request)
    {
        Current?.Completion.TrySetResult(null); // only one dialog at a time
        Current = request;
        Changed?.Invoke();
        return request.Completion.Task;
    }
}
