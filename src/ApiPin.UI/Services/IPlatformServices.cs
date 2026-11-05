namespace ApiPin.UI.Services;

/// <summary>Things the web view cannot do on its own; implemented by each host.</summary>
public interface IPlatformServices
{
    /// <summary>False when the host has no native file dialogs (e.g. a browser host).</summary>
    bool SupportsFileDialogs { get; }

    /// <summary>
    /// True when the UI runs in a regular browser, which keeps Ctrl+T, Ctrl+W and Ctrl+Tab for itself;
    /// the UI then advertises the Alt+T / Alt+W alternatives.
    /// </summary>
    bool RunsInBrowser { get; }

    /// <summary>Returns the chosen file's full path, or null when cancelled.</summary>
    Task<string?> PickFileAsync();

    /// <summary>Asks where to save and writes the file. Returns the path, or null when cancelled.</summary>
    Task<string?> SaveFileAsync(string suggestedFileName, byte[] content);

    /// <summary>Returns the chosen folder's full path, or null when cancelled.</summary>
    Task<string?> PickFolderAsync();
}

/// <summary>Fallback for hosts without native dialogs; the UI then downloads files through the browser.</summary>
public sealed class BrowserPlatformServices : IPlatformServices
{
    public bool SupportsFileDialogs => false;
    public bool RunsInBrowser => true;
    public Task<string?> PickFileAsync() => Task.FromResult<string?>(null);
    public Task<string?> SaveFileAsync(string suggestedFileName, byte[] content) => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
}
