using System.Text;
using System.Text.RegularExpressions;
using ApiPin.Core.Models;

namespace ApiPin.Core.Http;

/// <summary>
/// Tolerant URL helpers that work on the text the user typed. Nothing here goes through
/// <see cref="Uri"/>, so half-typed URLs and unusual encodings survive editing untouched.
/// </summary>
public static partial class UrlTools
{
    /// <summary>Splits a URL into the part before '?', the query (without '?') and the fragment (without '#').</summary>
    public static (string BeforeQuery, string? Query, string? Fragment) Split(string url)
    {
        string? fragment = null;
        var hash = url.IndexOf('#');
        if (hash >= 0)
        {
            fragment = url[(hash + 1)..];
            url = url[..hash];
        }

        var q = url.IndexOf('?');
        return q >= 0 ? (url[..q], url[(q + 1)..], fragment) : (url, null, fragment);
    }

    /// <summary>Parses a raw query string into key/value pairs, exactly as written (no decoding).</summary>
    public static List<KeyValuePair<string, string>> ParseQuery(string? query)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(query)) return result;

        foreach (var pair in query.Split('&'))
        {
            if (pair.Length == 0) continue;
            var eq = pair.IndexOf('=');
            result.Add(eq >= 0 ? new(pair[..eq], pair[(eq + 1)..]) : new(pair, ""));
        }
        return result;
    }

    /// <summary>Rebuilds the URL with the enabled params as its query string.</summary>
    public static string WithQuery(string url, IEnumerable<KeyValueItem> queryParams)
    {
        var (before, _, fragment) = Split(url);
        var sb = new StringBuilder(before);

        var first = true;
        foreach (var p in queryParams)
        {
            if (!p.Enabled || (p.Key.Length == 0 && p.Value.Length == 0)) continue;
            sb.Append(first ? '?' : '&').Append(p.Key);
            if (p.Value.Length > 0) sb.Append('=').Append(p.Value);
            first = false;
        }

        if (fragment is not null) sb.Append('#').Append(fragment);
        return sb.ToString();
    }

    /// <summary>
    /// Re-reads the query rows from the URL after the user edited it. Descriptions are kept for
    /// rows whose key is unchanged, and disabled rows (which are not in the URL) are kept at the end.
    /// </summary>
    public static List<KeyValueItem> SyncQueryParams(string url, IReadOnlyList<KeyValueItem> existing)
    {
        var enabled = existing.Where(p => p.Enabled).ToList();
        var parsed = ParseQuery(Split(url).Query);

        var result = new List<KeyValueItem>(parsed.Count);
        for (var i = 0; i < parsed.Count; i++)
        {
            var (key, value) = parsed[i];
            var previous = i < enabled.Count && enabled[i].Key == key ? enabled[i] : null;
            result.Add(new KeyValueItem { Key = key, Value = value, Description = previous?.Description });
        }

        result.AddRange(existing.Where(p => !p.Enabled));
        return result;
    }

    /// <summary>Names of the <c>:name</c> path segments, in order, without duplicates.</summary>
    public static List<string> FindPathParams(string url)
    {
        var path = PathPart(Split(url).BeforeQuery);
        return PathParamRegex().Matches(path).Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    /// <summary>Re-reads the path param rows after a URL edit, keeping values for names that still exist.</summary>
    public static List<KeyValueItem> SyncPathParams(string url, IReadOnlyList<KeyValueItem> existing) =>
        FindPathParams(url)
            .Select(name => existing.FirstOrDefault(p => p.Key == name)?.Clone() ?? new KeyValueItem { Key = name })
            .ToList();

    /// <summary>Replaces <c>:name</c> segments with their escaped values. Params without a value are left as is.</summary>
    public static string ApplyPathParams(string url, IEnumerable<KeyValueItem> pathParams)
    {
        var values = pathParams
            .Where(p => p.Key.Length > 0 && p.Value.Length > 0)
            .GroupBy(p => p.Key)
            .ToDictionary(g => g.Key, g => g.First().Value);
        if (values.Count == 0) return url;

        var (before, query, fragment) = Split(url);
        var authorityEnd = before.Length - PathPart(before).Length;
        var path = PathParamRegex().Replace(
            before[authorityEnd..],
            m => values.TryGetValue(m.Groups[1].Value, out var v) ? "/" + Uri.EscapeDataString(v) : m.Value);

        var sb = new StringBuilder(before[..authorityEnd]).Append(path);
        if (query is not null) sb.Append('?').Append(query);
        if (fragment is not null) sb.Append('#').Append(fragment);
        return sb.ToString();
    }

    /// <summary>Adds "http://" when the URL has no scheme, like most API clients do.</summary>
    public static string EnsureScheme(string url) =>
        SchemeRegex().IsMatch(url) ? url : "http://" + url;

    /// <summary>Appends an already-unescaped query parameter, escaping it.</summary>
    public static string AppendQueryParam(string url, string key, string value)
    {
        var (before, query, fragment) = Split(url);
        var pair = Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value);
        var sb = new StringBuilder(before).Append('?');
        if (!string.IsNullOrEmpty(query)) sb.Append(query).Append('&');
        sb.Append(pair);
        if (fragment is not null) sb.Append('#').Append(fragment);
        return sb.ToString();
    }

    /// <summary>The path portion of a URL without query, i.e. everything after scheme://authority.</summary>
    private static string PathPart(string beforeQuery)
    {
        var start = 0;
        var scheme = beforeQuery.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) start = scheme + 3;

        var slash = beforeQuery.IndexOf('/', start);
        return slash >= 0 ? beforeQuery[slash..] : "";
    }

    [GeneratedRegex(@"/:([A-Za-z_][A-Za-z0-9_\-]*)(?=/|$)")]
    private static partial Regex PathParamRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*://")]
    private static partial Regex SchemeRegex();
}
