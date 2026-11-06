using System.Text.Json;
using ApiPin.Core.Models;

namespace ApiPin.Storage;

/// <summary>How much history to keep.</summary>
public sealed record HistorySettings
{
    public bool Enabled { get; init; } = true;
    public int MaxEntries { get; init; } = 500;
    public int MaxDays { get; init; } = 30;

    /// <summary>Keep response bodies (up to <see cref="MaxBodyKb"/>), so a restored entry shows its response.</summary>
    public bool SaveBodies { get; init; } = true;

    public int MaxBodyKb { get; init; } = 256;
}

/// <summary>One sent request: the request as edited (variables unresolved) and a summary of its response.</summary>
public sealed record HistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public string? Workspace { get; init; }
    public Guid? SavedRequestId { get; init; }
    public string? Environment { get; init; }

    public required RequestDefinition Request { get; init; }

    /// <summary>Method and URL as sent (variables resolved).</summary>
    public string Method { get; init; } = "";
    public string Url { get; init; } = "";

    public int? Status { get; init; }
    public string? Reason { get; init; }
    public string? Error { get; init; }
    public double DurationMs { get; init; }
    public long SizeBytes { get; init; }
    public string? ContentType { get; init; }
    public List<KeyValuePair<string, string>> ResponseHeaders { get; init; } = [];

    /// <summary>The (possibly cut) response body, base64; null when bodies aren't kept.</summary>
    public string? Body { get; init; }
    public bool BodyTruncated { get; init; }

    public static HistoryEntry From(RequestDefinition request, ResponseResult response, HistorySettings settings, string? workspace, Guid? savedId, string? environment)
    {
        var max = settings.MaxBodyKb * 1024;
        var keep = settings.SaveBodies && response.Body.Length > 0;
        return new HistoryEntry
        {
            Workspace = workspace,
            SavedRequestId = savedId,
            Environment = environment,
            Request = request.Clone(newId: false),
            Method = response.Request?.Method ?? request.Method,
            Url = response.Request?.Url ?? request.Url,
            Status = response.StatusCode,
            Reason = response.ReasonPhrase,
            Error = response.Error?.Title,
            DurationMs = response.Timings.Total.TotalMilliseconds,
            SizeBytes = response.SizeBytes,
            ContentType = response.ContentType,
            ResponseHeaders = response.Headers.ToList(),
            Body = keep ? Convert.ToBase64String(response.Body.AsSpan(0, Math.Min(response.Body.Length, max))) : null,
            BodyTruncated = keep && (response.Body.Length > max || response.BodyTruncated),
            At = response.ReceivedAt,
        };
    }

    /// <summary>A response to show again when the entry is restored (body only if it was kept).</summary>
    public ResponseResult ToResponse() => Error is not null
        ? ResponseResult.Failed(new ExecutionError(ErrorKind.Unknown, Error, "From history; send again for details."), null, new TimingInfo(TimeSpan.FromMilliseconds(DurationMs), TimeSpan.Zero, TimeSpan.Zero))
        : new ResponseResult
        {
            StatusCode = Status,
            ReasonPhrase = Reason,
            Headers = ResponseHeaders,
            ContentType = ContentType,
            Body = Body is null ? [] : Convert.FromBase64String(Body),
            BodyTruncated = BodyTruncated,
            SizeBytes = SizeBytes,
            Timings = new TimingInfo(TimeSpan.FromMilliseconds(DurationMs), TimeSpan.Zero, TimeSpan.Zero),
            Request = new SentRequestInfo(Method, Url, "", [], null),
            ReceivedAt = At,
        };
}

/// <summary>
/// Sent requests in app data (<c>history.jsonl</c>, one entry per line, so adding is an append).
/// Trimmed to the retention settings now and then; unreadable lines are skipped.
/// </summary>
public sealed class HistoryStore(string filePath)
{
    private readonly Lock _lock = new();
    private List<HistoryEntry>? _entries;
    private int _appendedSinceTrim;

    public static string DefaultPath => AppDataPaths.File("history.jsonl");

    public event Action? Changed;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (_lock) return [.. Loaded()];
        }
    }

    public void Add(HistoryEntry entry, HistorySettings settings)
    {
        if (!settings.Enabled) return;
        lock (_lock)
        {
            var entries = Loaded();
            entries.Insert(0, entry);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
                File.AppendAllText(filePath, JsonSerializer.Serialize(entry, Compact) + "\n");
                if (++_appendedSinceTrim >= 50 || entries.Count > settings.MaxEntries * 1.2) Trim(entries, settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // History is a convenience; a disk problem must not break sending.
            }
        }
        Changed?.Invoke();
    }

    public void Delete(Guid id)
    {
        lock (_lock)
        {
            var entries = Loaded();
            if (entries.RemoveAll(e => e.Id == id) > 0) Rewrite(entries);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries = [];
            try { File.Delete(filePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    /// <summary>Applies the retention settings now (e.g. after they changed).</summary>
    public void Apply(HistorySettings settings)
    {
        lock (_lock) Trim(Loaded(), settings);
        Changed?.Invoke();
    }

    private void Trim(List<HistoryEntry> entries, HistorySettings settings)
    {
        _appendedSinceTrim = 0;
        var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(1, settings.MaxDays));
        var before = entries.Count;
        entries.RemoveAll(e => e.At < cutoff);
        if (entries.Count > settings.MaxEntries) entries.RemoveRange(settings.MaxEntries, entries.Count - settings.MaxEntries);
        if (entries.Count != before) Rewrite(entries);
    }

    private void Rewrite(List<HistoryEntry> entries)
    {
        try
        {
            AtomicFile.WriteAllText(filePath, string.Concat(Enumerable.Reverse(entries).Select(e => JsonSerializer.Serialize(e, Compact) + "\n")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private List<HistoryEntry> Loaded()
    {
        if (_entries is not null) return _entries;
        var entries = new List<HistoryEntry>();
        try
        {
            if (File.Exists(filePath))
            {
                foreach (var line in File.ReadLines(filePath))
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<HistoryEntry>(line, Compact) is { } entry) entries.Add(entry);
                    }
                    catch (JsonException)
                    {
                        // A half-written or old-format line: skip it.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        entries.Reverse();
        return _entries = entries;
    }

    private static readonly JsonSerializerOptions Compact = new(JsonDefaults.Options) { WriteIndented = false };
}
