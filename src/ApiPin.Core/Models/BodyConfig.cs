using System.Text.Json.Serialization;

namespace ApiPin.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoBody), "none")]
[JsonDerivedType(typeof(RawBody), "raw")]
[JsonDerivedType(typeof(FormUrlEncodedBody), "form")]
[JsonDerivedType(typeof(MultipartBody), "multipart")]
[JsonDerivedType(typeof(BinaryFileBody), "binary")]
public abstract class BodyConfig
{
    public abstract BodyConfig Clone();
}

public sealed class NoBody : BodyConfig
{
    public override BodyConfig Clone() => new NoBody();
}

public enum RawLanguage { Json, Xml, Text, Html, JavaScript }

public sealed class RawBody : BodyConfig
{
    public RawLanguage Language { get; set; } = RawLanguage.Json;
    public string Content { get; set; } = "";

    [JsonIgnore]
    public string MediaType => Language switch
    {
        RawLanguage.Json => "application/json",
        RawLanguage.Xml => "application/xml",
        RawLanguage.Html => "text/html",
        RawLanguage.JavaScript => "application/javascript",
        _ => "text/plain",
    };

    public override BodyConfig Clone() => new RawBody { Language = Language, Content = Content };
}

public sealed class FormUrlEncodedBody : BodyConfig
{
    public List<KeyValueItem> Fields { get; set; } = [];

    public override BodyConfig Clone() => new FormUrlEncodedBody { Fields = Fields.ConvertAll(f => f.Clone()) };
}

public enum MultipartPartKind { Text, File }

public sealed class MultipartPart
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public MultipartPartKind Kind { get; set; } = MultipartPartKind.Text;

    /// <summary>Text value, or the file path when <see cref="Kind"/> is File.</summary>
    public string Value { get; set; } = "";

    /// <summary>Optional explicit content type for the part.</summary>
    public string? ContentType { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Name.Length == 0 && Value.Length == 0 && string.IsNullOrEmpty(ContentType);

    public MultipartPart Clone() => (MultipartPart)MemberwiseClone();
}

public sealed class MultipartBody : BodyConfig
{
    public List<MultipartPart> Parts { get; set; } = [];

    public override BodyConfig Clone() => new MultipartBody { Parts = Parts.ConvertAll(p => p.Clone()) };
}

public sealed class BinaryFileBody : BodyConfig
{
    public string FilePath { get; set; } = "";
    public string? ContentType { get; set; }

    public override BodyConfig Clone() => new BinaryFileBody { FilePath = FilePath, ContentType = ContentType };
}
