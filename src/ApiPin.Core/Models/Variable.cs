using System.Text.Json.Serialization;

namespace ApiPin.Core.Models;

/// <summary>A named value usable as <c>{{key}}</c>: in an environment, a collection, a folder or the workspace globals.</summary>
public sealed class Variable
{
    public bool Enabled { get; set; } = true;
    public string Key { get; set; } = "";

    /// <summary>The value. For a <see cref="Secret"/> variable this is empty on disk; the value lives in the secret store.</summary>
    public string Value { get; set; } = "";

    /// <summary>Masked in the UI and kept out of workspace files.</summary>
    public bool Secret { get; set; }

    public string? Description { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Key.Length == 0 && Value.Length == 0 && string.IsNullOrEmpty(Description) && !Secret;

    [JsonIgnore]
    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Key);

    public Variable Clone() => (Variable)MemberwiseClone();
}
