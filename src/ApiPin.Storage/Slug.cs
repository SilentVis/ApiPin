using System.Text;

namespace ApiPin.Storage;

/// <summary>File-name-safe, lower-case names for collections, folders and requests on disk.</summary>
public static class Slug
{
    private const int MaxLength = 60;

    private static readonly HashSet<string> ReservedOnWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    public static string From(string name, string fallback)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= MaxLength) break;
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0) return fallback;
        return ReservedOnWindows.Contains(slug) ? "_" + slug : slug;
    }
}
