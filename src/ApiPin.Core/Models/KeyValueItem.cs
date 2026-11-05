using System.Text.Json.Serialization;

namespace ApiPin.Core.Models;

/// <summary>A row in a key/value grid: query params, path params, headers, form fields.</summary>
public sealed class KeyValueItem
{
    public bool Enabled { get; set; } = true;
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Description { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Key.Length == 0 && Value.Length == 0 && string.IsNullOrEmpty(Description);

    /// <summary>True when the row takes part in the request: enabled and has a key.</summary>
    [JsonIgnore]
    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Key);

    public KeyValueItem Clone() => (KeyValueItem)MemberwiseClone();
}
