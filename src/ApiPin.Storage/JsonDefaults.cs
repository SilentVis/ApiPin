using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ApiPin.Core.Models;

namespace ApiPin.Storage;

public static class JsonDefaults
{
    /// <summary>Stable, readable, Git-friendly JSON: camelCase, 2-space indent, enums as strings.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SkipEmptyOptionalLists } },
    };

    /// <summary>Request files leave out captures and assertions when there are none, so most files stay unchanged.</summary>
    private static void SkipEmptyOptionalLists(JsonTypeInfo info)
    {
        if (info.Type != typeof(RequestDefinition)) return;
        foreach (var property in info.Properties)
        {
            if (property.Name is "captures" or "assertions")
                property.ShouldSerialize = (_, value) => value is ICollection { Count: > 0 };
        }
    }
}
