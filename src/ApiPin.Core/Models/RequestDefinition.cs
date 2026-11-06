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
    /// <summary>Inherited from the folder/collection by default; a request outside a collection then has no auth.</summary>
    public AuthConfig Auth { get; set; } = new InheritAuth();
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
        Settings = Settings.Copy(),
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

    /// <summary>Send cookies from the environment's cookie jar and store cookies the response sets.</summary>
    public bool UseCookieJar { get; set; } = true;

    /// <summary>Client certificate for mutual TLS; null for none.</summary>
    public ClientCertificateSettings? ClientCertificate { get; set; }

    public RequestSettings Copy() => this with { ClientCertificate = ClientCertificate is { } c ? c with { } : null };
}

public enum ClientCertificateSource { File, Store }

/// <summary>A client certificate: a PFX/P12 file with its password, or one in the Windows certificate store by thumbprint.</summary>
public sealed record ClientCertificateSettings
{
    public ClientCertificateSource Source { get; set; } = ClientCertificateSource.File;
    public string FilePath { get; set; } = "";

    /// <summary>PFX password; use a secret variable such as <c>{{certPassword}}</c> to keep it out of Git.</summary>
    public string Password { get; set; } = "";

    public string Thumbprint { get; set; } = "";
}
