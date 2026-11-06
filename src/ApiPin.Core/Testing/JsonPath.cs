using System.Text.Json;

namespace ApiPin.Core.Testing;

public sealed class JsonPathException(string message) : Exception(message);

/// <summary>
/// A JSONPath subset for captures and assertions: <c>$</c>, <c>.name</c>, <c>['name']</c>, <c>[0]</c>, <c>[-1]</c>,
/// <c>[*]</c> / <c>.*</c>, and <c>..name</c> (recursive descent). Filters and slices aren't supported.
/// </summary>
public static class JsonPath
{
    public static IReadOnlyList<JsonElement> Select(JsonElement root, string path)
    {
        path = path.Trim();
        if (path.Length == 0 || path == "$") return [root];
        if (path[0] != '$') path = "$." + path.TrimStart('.'); // "data.id" works too

        IEnumerable<JsonElement> current = [root];
        var i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                var recursive = i + 1 < path.Length && path[i + 1] == '.';
                i += recursive ? 2 : 1;
                if (i < path.Length && path[i] == '[')
                {
                    if (recursive) current = current.SelectMany(Descendants);
                    continue;
                }
                var start = i;
                while (i < path.Length && path[i] is not ('.' or '[')) i++;
                var name = path[start..i];
                if (name.Length == 0) throw new JsonPathException($"Expected a property name at position {start} of '{path}'.");
                current = recursive
                    ? current.SelectMany(Descendants).SelectMany(e => Child(e, name))
                    : current.SelectMany(e => Child(e, name));
            }
            else if (path[i] == '[')
            {
                var end = FindClose(path, i);
                var inside = path[(i + 1)..end].Trim();
                i = end + 1;
                if (inside == "*") current = current.SelectMany(e => Child(e, "*"));
                else if (inside.Length >= 2 && inside[0] is '\'' or '"' && inside[^1] == inside[0])
                    current = current.SelectMany(e => Child(e, inside[1..^1]));
                else if (int.TryParse(inside, out var index)) current = current.SelectMany(e => Index(e, index));
                else if (inside.Contains(','))
                {
                    var keys = inside.Split(',').Select(k => k.Trim().Trim('\'', '"')).ToList();
                    current = current.SelectMany(e => keys.SelectMany(k => int.TryParse(k, out var n) ? Index(e, n) : Child(e, k)));
                }
                else throw new JsonPathException($"Unsupported selector [{inside}] in '{path}' (filters and slices aren't supported).");
            }
            else
            {
                throw new JsonPathException($"Unexpected '{path[i]}' at position {i} of '{path}'.");
            }
        }
        return current.ToList();
    }

    private static int FindClose(string path, int open)
    {
        char? quote = null;
        for (var i = open + 1; i < path.Length; i++)
        {
            var c = path[i];
            if (quote is not null) { if (c == quote) quote = null; }
            else if (c is '\'' or '"') quote = c;
            else if (c == ']') return i;
        }
        throw new JsonPathException($"Missing ']' in '{path}'.");
    }

    private static IEnumerable<JsonElement> Child(JsonElement e, string name)
    {
        if (name == "*")
        {
            if (e.ValueKind == JsonValueKind.Object) return e.EnumerateObject().Select(p => p.Value).ToList();
            if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray().ToList();
            return [];
        }
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? [v] : [];
    }

    private static IEnumerable<JsonElement> Index(JsonElement e, int index)
    {
        if (e.ValueKind != JsonValueKind.Array) return [];
        var length = e.GetArrayLength();
        if (index < 0) index += length;
        return index >= 0 && index < length ? [e[index]] : [];
    }

    /// <summary>The element and everything below it.</summary>
    private static IEnumerable<JsonElement> Descendants(JsonElement e)
    {
        yield return e;
        var children = e.ValueKind switch
        {
            JsonValueKind.Object => e.EnumerateObject().Select(p => p.Value),
            JsonValueKind.Array => e.EnumerateArray(),
            _ => [],
        };
        foreach (var child in children)
            foreach (var d in Descendants(child)) yield return d;
    }

    /// <summary>A value as text: strings unquoted, everything else as compact JSON.</summary>
    public static string Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null => "null",
        _ => e.GetRawText(),
    };
}
