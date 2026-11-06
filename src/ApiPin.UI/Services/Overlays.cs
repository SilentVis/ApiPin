namespace ApiPin.UI.Services;

public enum Overlay { None, Environments, Settings, Import, Snippet, Cookies, Palette, Shortcuts }

/// <summary>Which full-window dialog is open (one at a time); <c>ApiPinApp</c> renders it.</summary>
public sealed class Overlays
{
    public Overlay Current { get; private set; }

    /// <summary>Text handed to the dialog, e.g. a pasted command for Import.</summary>
    public string? Argument { get; private set; }

    public event Action? Changed;

    public void Show(Overlay overlay, string? argument = null)
    {
        Current = overlay;
        Argument = argument;
        Changed?.Invoke();
    }

    public void Close() => Show(Overlay.None);
}
