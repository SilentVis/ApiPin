using ApiPin.Core.Models;

namespace ApiPin.UI.State;

public sealed class RequestTab(RequestDefinition request)
{
    public RequestDefinition Request { get; } = request;
    public ResponseResult? Response { get; internal set; }
    public bool IsSending { get; internal set; }
    internal CancellationTokenSource? Sending { get; set; }

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
