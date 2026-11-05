using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Xml;
using System.Xml.Linq;

namespace ApiPin.Core.Formatting;

public enum BodyKind { Empty, Json, Xml, Html, Text, Image, Binary }

public static class BodyFormatter
{
    private static readonly JsonWriterOptions PrettyJsonOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static BodyKind Detect(string? contentType, byte[] body)
    {
        if (body.Length == 0) return BodyKind.Empty;

        var mediaType = contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? "";
        if (mediaType.Contains("json")) return BodyKind.Json;
        if (mediaType.Contains("html")) return BodyKind.Html;
        if (mediaType.StartsWith("image/")) return mediaType.Contains("svg") ? BodyKind.Xml : BodyKind.Image;
        if (mediaType.Contains("xml")) return BodyKind.Xml;
        if (mediaType.StartsWith("text/") || mediaType.Contains("javascript") || mediaType.Contains("x-www-form-urlencoded"))
            return BodyKind.Text;

        // Unknown or missing type: sniff.
        if (!LooksLikeText(body)) return BodyKind.Binary;
        var start = Decode(body, null).TrimStart();
        if (start.StartsWith('{') || start.StartsWith('[')) return BodyKind.Json;
        if (start.StartsWith('<')) return start.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            ? BodyKind.Html
            : BodyKind.Xml;
        return BodyKind.Text;
    }

    /// <summary>Decodes text using the charset from the content type, UTF-8 otherwise.</summary>
    public static string Decode(byte[] body, string? contentType)
    {
        var encoding = Encoding.UTF8;
        var charset = contentType?.Split(';')
            .Select(p => p.Trim())
            .FirstOrDefault(p => p.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))?[8..].Trim('"');
        if (charset is not null)
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch (ArgumentException) { }
        }
        return encoding.GetString(body);
    }

    /// <summary>Pretty-prints JSON or XML; returns the text unchanged when it can't be parsed.</summary>
    public static string Pretty(string text, BodyKind kind) => kind switch
    {
        BodyKind.Json => TryPrettyJson(text) ?? text,
        BodyKind.Xml => TryPrettyXml(text) ?? text,
        _ => text,
    };

    public static string? TryPrettyJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, PrettyJsonOptions)) doc.WriteTo(writer);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? TryPrettyXml(string text)
    {
        try
        {
            var doc = XDocument.Parse(text);
            var body = doc.ToString(SaveOptions.None);
            return doc.Declaration is null ? body : doc.Declaration + Environment.NewLine + body;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.##} MB",
    };

    private static bool LooksLikeText(byte[] body)
    {
        var sample = body.AsSpan(0, Math.Min(body.Length, 4096));
        return !sample.Contains((byte)0) && Utf8.IsValid(sample[..Utf8SafeLength(sample)]);
    }

    /// <summary>Excludes a multi-byte UTF-8 sequence cut off at the end of the sample.</summary>
    private static int Utf8SafeLength(ReadOnlySpan<byte> sample)
    {
        var end = sample.Length;
        for (var i = 1; i <= 3 && i <= sample.Length; i++)
        {
            var b = sample[^i];
            if ((b & 0xC0) == 0x80) continue;      // continuation byte
            if ((b & 0x80) != 0) end = sample.Length - i; // lead byte: drop the possibly incomplete sequence
            break;
        }
        return end;
    }
}
