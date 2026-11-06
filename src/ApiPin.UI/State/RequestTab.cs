using ApiPin.Core.Models;
using ApiPin.Core.Variables;

namespace ApiPin.UI.State;

public sealed class RequestTab(RequestDefinition request)
{
    public RequestDefinition Request { get; internal set; } = request;
    public ResponseResult? Response { get; internal set; }
    public bool IsSending { get; internal set; }
    internal CancellationTokenSource? Sending { get; set; }

    /// <summary>JSON of the saved request this tab edits, or null for an unsaved (scratch) tab.</summary>
    internal string? SavedJson { get; set; }

    /// <summary>True when the tab has edits that are not saved to its collection yet.</summary>
    public bool IsModified { get; internal set; }

    /// <summary>The saved request changed on disk (e.g. a git pull) while this tab had unsaved edits.</summary>
    public bool HasConflict { get; internal set; }

    /// <summary>Variable problems found when the response was sent (unresolved or cyclic names).</summary>
    public ResolveReport? LastReport { get; internal set; }

    /// <summary>Captures and assertions of the last response.</summary>
    public ApiPin.Core.Testing.TestReport? TestReport { get; internal set; }

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
