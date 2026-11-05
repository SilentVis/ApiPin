using ApiPin.Core.Models;

namespace ApiPin.UI.State;

public sealed class RequestTab(RequestDefinition request)
{
    public RequestDefinition Request { get; } = request;
    public ResponseResult? Response { get; internal set; }
    public bool IsSending { get; internal set; }
    internal CancellationTokenSource? Sending { get; set; }

    /// <summary>JSON of the saved request this tab edits, or null for an unsaved (scratch) tab.</summary>
    internal string? SavedJson { get; set; }

    /// <summary>True when the tab has edits that are not saved to its collection yet.</summary>
    public bool IsModified { get; internal set; }

    public string RequestSection { get; set; } = "params";
    public string ResponseSection { get; set; } = "body";

    /// <summary>Settings of other auth/body types, so switching type and back doesn't lose what was typed.</summary>
    public Dictionary<Type, AuthConfig> AuthCache { get; } = [];
    public Dictionary<Type, BodyConfig> BodyCache { get; } = [];

    public string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Request.Name)) return Request.Name;
            var url = Request.Url.Trim();
            if (url.Length == 0) return "Untitled request";
            var scheme = url.IndexOf("://", StringComparison.Ordinal);
            return scheme >= 0 ? url[(scheme + 3)..] : url;
        }
    }
}
