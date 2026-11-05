using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ApiPin.Core.Formatting;

namespace ApiPin.UI.Services;

/// <summary>Turns body text into HTML with token spans (tk-*) or search marks. Output is always HTML-escaped.</summary>
public static partial class SyntaxHighlighter
{
    /// <summary>Above this size the body is shown as plain text, to keep rendering fast.</summary>
    public const int MaxHighlightChars = 400_000;

    public const int MaxSearchMarks = 2_000;

    public static string Highlight(string text, BodyKind kind)
    {
        if (text.Length > MaxHighlightChars) return Escape(text);
        return kind switch
        {
            BodyKind.Json => Json(text),
            BodyKind.Xml or BodyKind.Html => Markup(text),
            _ => Escape(text),
        };
    }

    /// <summary>Escaped text with each case-insensitive match wrapped in &lt;mark&gt;.</summary>
    public static (string Html, int Count) Search(string text, string term)
    {
        var sb = new StringBuilder(text.Length + 64);
        var count = 0;
        var pos = 0;
        while (count < MaxSearchMarks)
        {
            var hit = text.IndexOf(term, pos, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;
            Append(sb, text.AsSpan(pos, hit - pos));
            sb.Append("<mark>");
            Append(sb, text.AsSpan(hit, term.Length));
            sb.Append("</mark>");
            pos = hit + term.Length;
            count++;
        }
        Append(sb, text.AsSpan(pos));
        return (sb.ToString(), count);
    }

    public static int CountMatches(string text, string term)
    {
        var count = 0;
        for (var pos = 0; count < MaxSearchMarks; count++)
        {
            var hit = text.IndexOf(term, pos, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;
            pos = hit + term.Length;
        }
        return count;
    }

    private static string Json(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                var start = i++;
                while (i < text.Length && text[i] != '"') i += text[i] == '\\' ? 2 : 1;
                i = Math.Min(i + 1, text.Length);

                var next = i;
                while (next < text.Length && text[next] is ' ' or '\t') next++;
                Span(sb, next < text.Length && text[next] == ':' ? "tk-key" : "tk-str", text.AsSpan(start, i - start));
            }
            else if (c == '-' || char.IsAsciiDigit(c))
            {
                var start = i++;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] is '.' or 'e' or 'E' or '+' or '-')) i++;
                Span(sb, "tk-num", text.AsSpan(start, i - start));
            }
            else if (char.IsAsciiLetter(c))
            {
                var start = i++;
                while (i < text.Length && char.IsAsciiLetter(text[i])) i++;
                var word = text.AsSpan(start, i - start);
                if (word is "true" or "false") Span(sb, "tk-bool", word);
                else if (word is "null") Span(sb, "tk-null", word);
                else Append(sb, word);
            }
            else
            {
                Append(sb, text.AsSpan(i, 1));
                i++;
            }
        }
        return sb.ToString();
    }

    private static string Markup(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('<', i);
            if (open < 0)
            {
                Append(sb, text.AsSpan(i));
                break;
            }
            Append(sb, text.AsSpan(i, open - i));

            if (text.AsSpan(open).StartsWith("<!--"))
            {
                var end = text.IndexOf("-->", open, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 3;
                Span(sb, "tk-comment", text.AsSpan(open, end - open));
                i = end;
                continue;
            }

            var close = text.IndexOf('>', open);
            close = close < 0 ? text.Length : close + 1;
            Tag(sb, text[open..close]);
            i = close;
        }
        return sb.ToString();
    }

    private static void Tag(StringBuilder sb, string tag)
    {
        var match = TagNameRegex().Match(tag);
        if (!match.Success)
        {
            Append(sb, tag);
            return;
        }

        Span(sb, "tk-punc", match.Groups[1].ValueSpan);
        Span(sb, "tk-tag", match.Groups[2].ValueSpan);

        var rest = tag[match.Length..];
        var pos = 0;
        foreach (Match attr in AttributeRegex().Matches(rest))
        {
            Append(sb, rest.AsSpan(pos, attr.Index - pos));
            Span(sb, "tk-attr", attr.Groups[1].ValueSpan);
            Append(sb, attr.Groups[2].ValueSpan);
            Span(sb, "tk-str", attr.Groups[3].ValueSpan);
            pos = attr.Index + attr.Length;
        }

        var tail = rest.AsSpan(pos);
        var punct = tail.EndsWith("/>") || tail.EndsWith("?>") ? 2 : tail.EndsWith(">") ? 1 : 0;
        Append(sb, tail[..^punct]);
        Span(sb, "tk-punc", tail[^punct..]);
    }

    private static void Span(StringBuilder sb, string cls, ReadOnlySpan<char> token)
    {
        if (token.IsEmpty) return;
        sb.Append("<span class=\"").Append(cls).Append("\">");
        Append(sb, token);
        sb.Append("</span>");
    }

    private static void Append(StringBuilder sb, ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    [GeneratedRegex(@"^(</?|<\?|<!)([\w:.\-]*)")]
    private static partial Regex TagNameRegex();

    [GeneratedRegex(@"([\w:.\-]+)(\s*=\s*)(""[^""]*""|'[^']*')")]
    private static partial Regex AttributeRegex();
}
