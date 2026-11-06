using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApiPin.Core.Testing;

/// <summary>
/// A small JSON Schema validator covering what API responses usually need: type, required, properties,
/// additionalProperties (false), items, enum, const, minimum / maximum, minLength / maxLength, minItems / maxItems,
/// pattern, allOf / anyOf / oneOf, and local <c>$ref</c>s into <c>#/definitions</c> or <c>#/$defs</c>.
/// </summary>
public static class JsonSchemaValidator
{
    /// <summary>The problems found (each with a JSON path); empty when the value matches.</summary>
    public static IReadOnlyList<string> Validate(JsonElement value, JsonElement schema)
    {
        var errors = new List<string>();
        Check(value, schema, schema, "$", errors, 0);
        return errors;
    }

    private static void Check(JsonElement value, JsonElement schema, JsonElement root, string path, List<string> errors, int depth)
    {
        if (depth > 64 || schema.ValueKind != JsonValueKind.Object) return;

        if (schema.TryGetProperty("$ref", out var reference) && reference.GetString() is { } r)
        {
            if (Resolve(root, r) is { } target) Check(value, target, root, path, errors, depth + 1);
            else errors.Add($"{path}: can't resolve $ref '{r}'.");
            return;
        }

        if (schema.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()).ToList() : [type.GetString()];
            if (!types.Any(t => IsType(value, t))) errors.Add($"{path}: expected {string.Join(" or ", types)}, got {Kind(value)}.");
        }

        if (schema.TryGetProperty("enum", out var options) && !options.EnumerateArray().Any(o => JsonEquals(o, value)))
            errors.Add($"{path}: {Short(value)} is not one of {options.GetRawText()}.");
        if (schema.TryGetProperty("const", out var constant) && !JsonEquals(constant, value))
            errors.Add($"{path}: expected {constant.GetRawText()}, got {Short(value)}.");

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (schema.TryGetProperty("required", out var required))
                    foreach (var name in required.EnumerateArray().Select(n => n.GetString()!))
                        if (!value.TryGetProperty(name, out _)) errors.Add($"{path}: missing required property '{name}'.");
                var properties = schema.TryGetProperty("properties", out var props) ? props : default;
                foreach (var p in value.EnumerateObject())
                {
                    if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(p.Name, out var sub))
                        Check(p.Value, sub, root, $"{path}.{p.Name}", errors, depth + 1);
                    else if (schema.TryGetProperty("additionalProperties", out var extra))
                    {
                        if (extra.ValueKind == JsonValueKind.False) errors.Add($"{path}: unexpected property '{p.Name}'.");
                        else if (extra.ValueKind == JsonValueKind.Object) Check(p.Value, extra, root, $"{path}.{p.Name}", errors, depth + 1);
                    }
                }
                break;

            case JsonValueKind.Array:
                var count = value.GetArrayLength();
                if (Number(schema, "minItems") is { } minItems && count < minItems) errors.Add($"{path}: {count} items, at least {minItems} expected.");
                if (Number(schema, "maxItems") is { } maxItems && count > maxItems) errors.Add($"{path}: {count} items, at most {maxItems} expected.");
                if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
                {
                    var i = 0;
                    foreach (var item in value.EnumerateArray()) Check(item, items, root, $"{path}[{i++}]", errors, depth + 1);
                }
                break;

            case JsonValueKind.String:
                var s = value.GetString()!;
                if (Number(schema, "minLength") is { } minLength && s.Length < minLength) errors.Add($"{path}: shorter than {minLength} characters.");
                if (Number(schema, "maxLength") is { } maxLength && s.Length > maxLength) errors.Add($"{path}: longer than {maxLength} characters.");
                if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(s, pattern.GetString()!, RegexOptions.None, TimeSpan.FromSeconds(1)))
                    errors.Add($"{path}: '{s}' doesn't match /{pattern.GetString()}/.");
                break;

            case JsonValueKind.Number:
                var n = value.GetDouble();
                if (Number(schema, "minimum") is { } min && n < min) errors.Add($"{path}: {n} is less than {min}.");
                if (Number(schema, "maximum") is { } max && n > max) errors.Add($"{path}: {n} is more than {max}.");
                break;
        }

        if (schema.TryGetProperty("allOf", out var allOf))
            foreach (var s in allOf.EnumerateArray()) Check(value, s, root, path, errors, depth + 1);
        if (schema.TryGetProperty("anyOf", out var anyOf) && !anyOf.EnumerateArray().Any(s => Validate(value, s, root, depth).Count == 0))
            errors.Add($"{path}: matches none of anyOf.");
        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.EnumerateArray().Count(s => Validate(value, s, root, depth).Count == 0) != 1)
            errors.Add($"{path}: must match exactly one of oneOf.");
    }

    private static List<string> Validate(JsonElement value, JsonElement schema, JsonElement root, int depth)
    {
        var errors = new List<string>();
        Check(value, schema, root, "$", errors, depth + 1);
        return errors;
    }

    private static JsonElement? Resolve(JsonElement root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return reference == "#" ? root : null;
        var current = root;
        foreach (var part in reference[2..].Split('/'))
        {
            var name = part.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return null;
        }
        return current;
    }

    private static bool IsType(JsonElement value, string? type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => value.ValueKind.ToString().ToLowerInvariant(),
    };

    private static double? Number(JsonElement schema, string name) =>
        schema.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool JsonEquals(JsonElement a, JsonElement b) => JsonElement.DeepEquals(a, b);

    private static string Short(JsonElement e)
    {
        var text = e.GetRawText();
        return text.Length > 60 ? text[..60] + "…" : text;
    }
}
