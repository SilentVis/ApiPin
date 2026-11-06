namespace ApiPin.Core.Models;

public enum ErrorKind { InvalidRequest, Auth, DnsFailure, ConnectionRefused, Tls, Timeout, Cancelled, Network, Unknown }

public sealed record ExecutionError(ErrorKind Kind, string Title, string Detail, string? Hint = null);

public sealed record TimingInfo(TimeSpan Total, TimeSpan TimeToFirstByte, TimeSpan Download)
{
    public static readonly TimingInfo Zero = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
}

public sealed record ResponseCookie(
    string Name,
    string Value,
    string? Domain,
    string? Path,
    string? Expires,
    string? MaxAge,
    bool HttpOnly,
    bool Secure,
    string? SameSite);

/// <summary>What was actually sent, after auth and default headers were applied.</summary>
public sealed record SentRequestInfo(
    string Method,
    string Url,
    string HttpVersion,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    long? BodyLength);

public sealed record ResponseResult
{
    public int? StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public string? HttpVersion { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
    public IReadOnlyList<ResponseCookie> Cookies { get; init; } = [];

    /// <summary>Response body, decompressed; at most <see cref="Http.HttpExecutorOptions.MaxBodyBytes"/> bytes.</summary>
    public byte[] Body { get; init; } = [];

    public bool BodyTruncated { get; init; }

    /// <summary>Body size in bytes (the full size when known, even if <see cref="Body"/> was truncated).</summary>
    public long SizeBytes { get; init; }

    public string? ContentType { get; init; }
    public TimingInfo Timings { get; init; } = TimingInfo.Zero;
    public SentRequestInfo? Request { get; init; }
    public ExecutionError? Error { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;

    public bool IsError => Error is not null;

    public static ResponseResult Failed(ExecutionError error, SentRequestInfo? request, TimingInfo timings) =>
        new() { Error = error, Request = request, Timings = timings };
}
