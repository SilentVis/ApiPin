using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ApiPin.Core.Models;

namespace ApiPin.Core.Variables;

/// <summary>A <c>{{name}}</c> found in text.</summary>
public readonly record struct VariableReference(string Name, int Index, int Length)
{
    public bool IsDynamic => Name.StartsWith('$');
}

/// <summary>What happened while resolving: which variables were used, which were missing, which formed a cycle.</summary>
public sealed class ResolveReport
{
    public HashSet<string> Used { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Unresolved { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Cyclic { get; } = new(StringComparer.Ordinal);

    public bool HasProblems => Unresolved.Count > 0 || Cyclic.Count > 0;
}

/// <summary>
/// Replaces <c>{{name}}</c> with variable values and <c>{{$guid}}</c>-style dynamic values.
/// Values may reference other variables (<c>{{baseUrl}}/{{version}}</c>); cycles and very deep chains are left as written.
/// Unknown names are left as written too, and reported.
/// </summary>
public static partial class VariableResolver
{
    public const int MaxDepth = 10;

    /// <summary>Dynamic variables, available everywhere. A new value is generated for every occurrence.</summary>
    public static readonly IReadOnlyDictionary<string, string> DynamicVariables = new Dictionary<string, string>
    {
        ["$guid"] = "A new random GUID",
        ["$randomUUID"] = "A new random GUID (Postman name)",
        ["$timestamp"] = "Current Unix time in seconds",
        ["$isoTimestamp"] = "Current UTC time, ISO 8601",
        ["$randomInt"] = "Random integer 0–1000",
        ["$randomEmail"] = "Random e-mail address at example.com",
        ["$randomAlphaNumeric"] = "One random letter or digit",
    };

    public static IEnumerable<VariableReference> FindReferences(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{")) yield break;
        foreach (Match m in ReferenceRegex().Matches(text))
            yield return new VariableReference(m.Groups[1].Value, m.Index, m.Length);
    }

    public static bool HasReferences(string? text) => !string.IsNullOrEmpty(text) && text.Contains("{{") && ReferenceRegex().IsMatch(text);

    public static string Resolve(string? text, VariableContext context, ResolveReport? report = null) =>
        Resolve(text ?? "", context, report, [], 0);

    /// <summary>The value one name resolves to (nested references included), or null when it doesn't exist.</summary>
    public static string? ResolveName(string name, VariableContext context, ResolveReport? report = null) =>
        ResolveOne(name, context, report, [], 0);

    private static string Resolve(string text, VariableContext context, ResolveReport? report, HashSet<string> stack, int depth)
    {
        if (text.Length == 0 || !text.Contains("{{")) return text;

        return ReferenceRegex().Replace(text, m => ResolveOne(m.Groups[1].Value, context, report, stack, depth) ?? m.Value);
    }

    private static string? ResolveOne(string name, VariableContext context, ResolveReport? report, HashSet<string> stack, int depth)
    {
        if (context.Lookup(name) is { } variable)
        {
            if (depth >= MaxDepth || !stack.Add(name))
            {
                report?.Cyclic.Add(name);
                return null;
            }
            report?.Used.Add(name);
            var value = Resolve(variable.Value, context, report, stack, depth + 1);
            stack.Remove(name);
            return value;
        }

        if (Dynamic(name) is { } dynamic)
        {
            report?.Used.Add(name);
            return dynamic;
        }

        report?.Unresolved.Add(name);
        return null;
    }

    private static string? Dynamic(string name) => name switch
    {
        "$guid" or "$randomUUID" => Guid.NewGuid().ToString(),
        "$timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        "$isoTimestamp" => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        "$randomInt" => RandomNumberGenerator.GetInt32(0, 1001).ToString(),
        "$randomEmail" => $"user{RandomNumberGenerator.GetInt32(1000, 100_000)}@example.com",
        "$randomAlphaNumeric" => RandomAlphaNumeric(1),
        _ => null,
    };

    private static string RandomAlphaNumeric(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }

    /// <summary>
    /// A copy of the request with variables resolved in the URL, params, headers, auth fields and body
    /// (raw text, form fields, multipart parts and file paths).
    /// </summary>
    public static RequestDefinition ResolveRequest(RequestDefinition request, VariableContext context, ResolveReport? report = null)
    {
        string R(string s) => Resolve(s, context, report);
        KeyValueItem Item(KeyValueItem i) => i.IsActive ? new KeyValueItem { Enabled = i.Enabled, Key = R(i.Key), Value = R(i.Value), Description = i.Description } : i.Clone();

        var resolved = request.Clone(newId: false);
        resolved.Method = R(request.Method);
        resolved.Url = R(request.Url);
        resolved.QueryParams = request.QueryParams.ConvertAll(Item);
        resolved.PathParams = request.PathParams.ConvertAll(p => new KeyValueItem { Enabled = p.Enabled, Key = p.Key, Value = R(p.Value), Description = p.Description });
        resolved.Headers = request.Headers.ConvertAll(Item);
        resolved.Auth = request.Auth.Map(R);
        resolved.Body = request.Body.Map(R);
        return resolved;
    }

    // {{name}} with optional inner spaces; names may contain anything but braces and whitespace.
    [GeneratedRegex(@"\{\{\s*([^{}\s]+)\s*\}\}")]
    private static partial Regex ReferenceRegex();
}
