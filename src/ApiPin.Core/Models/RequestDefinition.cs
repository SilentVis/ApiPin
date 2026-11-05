namespace ApiPin.Core.Models;

public sealed class RequestDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Free text, so custom verbs are allowed.</summary>
    public string Method { get; set; } = "GET";

    /// <summary>Full URL including the query string; may contain <c>:pathParams</c>.</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// Mirrors the query string of <see cref="Url"/>. Enabled rows are always in the URL;
    /// disabled rows are kept here only. Use <see cref="Http.UrlTools"/> to keep them in sync.
    /// </summary>
    public List<KeyValueItem> QueryParams { get; set; } = [];

    /// <summary>Values for the <c>:name</c> segments found in the URL path.</summary>
    public List<KeyValueItem> PathParams { get; set; } = [];

    public List<KeyValueItem> Headers { get; set; } = [];
    public AuthConfig Auth { get; set; } = new NoAuth();
    public BodyConfig Body { get; set; } = new NoBody();
    public RequestSettings Settings { get; set; } = new();

    /// <summary>Order inside its folder when saved in a collection.</summary>
    public int Seq { get; set; }

    public RequestDefinition Clone(bool newId = true) => new()
    {
        Id = newId ? Guid.NewGuid() : Id,
        Name = Name,
        Seq = Seq,
        Method = Method,
        Url = Url,
        QueryParams = QueryParams.ConvertAll(p => p.Clone()),
        PathParams = PathParams.ConvertAll(p => p.Clone()),
        Headers = Headers.ConvertAll(h => h.Clone()),
        Auth = Auth.Clone(),
        Body = Body.Clone(),
        Settings = Settings with { },
    };
}

public sealed record RequestSettings
{
    public int TimeoutMs { get; set; } = 30_000;
    public bool FollowRedirects { get; set; } = true;
    public int MaxRedirects { get; set; } = 10;

    /// <summary>Turn off to accept self-signed local dev certificates.</summary>
    public bool VerifyTls { get; set; } = true;

    /// <summary>null = auto (HTTP/2 when the server offers it), or "1.1", "2", "3".</summary>
    public string? HttpVersion { get; set; }
}
