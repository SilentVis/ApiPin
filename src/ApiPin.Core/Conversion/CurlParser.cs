using System.Text.RegularExpressions;
using System.Text;
using ApiPin.Core.Http;
using ApiPin.Core.Models;

namespace ApiPin.Core.Conversion;

public sealed class CurlParseException(string message) : Exception(message);

/// <summary>
/// Turns a pasted <c>curl</c> command into a request. Understands bash quoting and line continuations, and the
/// Windows cmd form browsers produce ("Copy as cURL (cmd)", with ^ escapes). Unknown options are ignored.
/// </summary>
public static class CurlParser
{
    public static bool LooksLikeCurl(string? text) =>
        text is not null && text.TrimStart().StartsWith("curl ", StringComparison.OrdinalIgnoreCase);

    /// <exception cref="CurlParseException">It isn't a curl command or has no URL.</exception>
    public static RequestDefinition Parse(string command)
    {
        var args = Tokenize(command);
        if (args.Count == 0 || !args[0].Equals("curl", StringComparison.OrdinalIgnoreCase) && !args[0].EndsWith("curl.exe", StringComparison.OrdinalIgnoreCase))
            throw new CurlParseException("The text doesn't start with 'curl'.");

        string? url = null, method = null, user = null;
        var headers = new List<KeyValueItem>();
        var data = new List<(string Value, bool UrlEncode, bool Raw)>();
        var form = new List<MultipartPart>();
        bool get = false, head = false;
        var settings = new RequestSettings { FollowRedirects = false };

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : throw new CurlParseException($"{arg} needs a value.");

            // -XPOST / -H'..' short options glued to their value.
            if (arg.Length > 2 && arg[0] == '-' && arg[1] != '-' && "XHduFAebm".Contains(arg[1]))
            {
                args.Insert(i + 1, arg[2..]);
                arg = arg[..2];
            }

            switch (arg)
            {
                case "-X" or "--request": method = Next(); break;
                case "-H" or "--header": headers.Add(Header(Next())); break;
                case "-d" or "--data" or "--data-ascii": data.Add((Next(), false, false)); break;
                case "--data-raw": data.Add((Next(), false, true)); break;
                case "--data-binary": data.Add((Next(), false, false)); break;
                case "--data-urlencode": data.Add((Next(), true, true)); break;
                case "--json":
                    data.Add((Next(), false, false));
                    if (!headers.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))) headers.Add(new() { Key = "Content-Type", Value = "application/json" });
                    if (!headers.Any(h => h.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase))) headers.Add(new() { Key = "Accept", Value = "application/json" });
                    break;
                case "-F" or "--form" or "--form-string": form.Add(FormPart(Next(), arg == "--form-string")); break;
                case "-u" or "--user": user = Next(); break;
                case "-A" or "--user-agent": headers.Add(new() { Key = "User-Agent", Value = Next() }); break;
                case "-e" or "--referer": headers.Add(new() { Key = "Referer", Value = Next() }); break;
                case "-b" or "--cookie": headers.Add(new() { Key = "Cookie", Value = Next() }); break;
                case "--oauth2-bearer": headers.Add(new() { Key = "Authorization", Value = "Bearer " + Next() }); break;
                case "--url": url = Next(); break;
                case "-G" or "--get": get = true; break;
                case "-I" or "--head": head = true; break;
                case "-k" or "--insecure": settings.VerifyTls = false; break;
                case "-L" or "--location": settings.FollowRedirects = true; break;
                case "-m" or "--max-time":
                    if (double.TryParse(Next(), System.Globalization.CultureInfo.InvariantCulture, out var seconds)) settings.TimeoutMs = (int)(seconds * 1000);
                    break;
                case "--http1.1": settings.HttpVersion = "1.1"; break;
                case "--http2": settings.HttpVersion = "2"; break;
                case "--http3": settings.HttpVersion = "3"; break;
                // Options with a value we don't use.
                case "-o" or "--output" or "-x" or "--proxy" or "-w" or "--write-out" or "--connect-timeout" or "-c" or "--cookie-jar"
                    or "--cacert" or "--cert" or "--key" or "-T" or "--upload-file" or "-r" or "--range" or "--retry":
                    Next();
                    break;
                default:
                    if (!arg.StartsWith('-') && url is null) url = arg;
                    break; // flags like --compressed, -s, -v, -i
            }
        }

        if (url is null) throw new CurlParseException("No URL found in the curl command.");

        var request = new RequestDefinition { Url = url, Settings = settings, Auth = new NoAuth() };

        // Body
        if (get && data.Count > 0)
        {
            var query = string.Join("&", data.Select(d => d.UrlEncode ? EncodePair(d.Value) : d.Value));
            request.Url = UrlTools.Split(url).Query is null ? $"{url}?{query}" : $"{url}&{query}";
        }
        else if (form.Count > 0)
        {
            request.Body = new MultipartBody { Parts = form };
        }
        else if (data.Count > 0)
        {
            request.Body = Body(data, headers);
        }

        // Auth: -u, or an Authorization header we can turn into a typed auth.
        if (user is not null)
        {
            var colon = user.IndexOf(':');
            request.Auth = new BasicAuth { Username = colon >= 0 ? user[..colon] : user, Password = colon >= 0 ? user[(colon + 1)..] : "" };
        }
        else if (headers.FirstOrDefault(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) is { } authorization && AuthFromHeader(authorization.Value) is { } auth)
        {
            headers.Remove(authorization);
            request.Auth = auth;
        }

        // Content-Type is implied by the body type unless it says something else.
        if (request.Body is FormUrlEncodedBody or MultipartBody)
            headers.RemoveAll(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                                   (h.Value.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) || h.Value.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase)));
        if (request.Body is RawBody raw && headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) is { } ct && ct.Value.StartsWith(raw.MediaType, StringComparison.OrdinalIgnoreCase))
            headers.Remove(ct);

        request.Headers = headers;
        request.Method = (method ?? (head ? "HEAD" : request.Body is NoBody || get ? "GET" : "POST")).ToUpperInvariant();
        request.QueryParams = UrlTools.SyncQueryParams(request.Url, []);
        request.PathParams = UrlTools.SyncPathParams(request.Url, []);
        request.Name = DefaultName(request);
        return request;
    }

    private static BodyConfig Body(List<(string Value, bool UrlEncode, bool Raw)> data, List<KeyValueItem> headers)
    {
        var contentType = headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "";

        if (data.Count == 1 && data[0].Value.StartsWith('@') && !data[0].Raw)
            return new BinaryFileBody { FilePath = data[0].Value[1..], ContentType = contentType.Length > 0 ? contentType : null };

        var isForm = contentType.Length == 0 || contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
        var joined = string.Join("&", data.Select(d => d.UrlEncode ? EncodePair(d.Value) : d.Value));
        var trimmed = joined.TrimStart();
        if (isForm && !(trimmed.StartsWith('{') || trimmed.StartsWith('[') || trimmed.StartsWith('<')))
        {
            return new FormUrlEncodedBody
            {
                Fields = joined.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
                {
                    var eq = pair.IndexOf('=');
                    return new KeyValueItem
                    {
                        Key = Uri.UnescapeDataString((eq >= 0 ? pair[..eq] : pair).Replace('+', ' ')),
                        Value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "",
                    };
                }).ToList(),
            };
        }

        var language = contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || (contentType.Length == 0 && (trimmed.StartsWith('{') || trimmed.StartsWith('[')))
            ? RawLanguage.Json
            : contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) || (contentType.Length == 0 && trimmed.StartsWith('<')) ? RawLanguage.Xml
            : contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ? RawLanguage.Html
            : contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ? RawLanguage.JavaScript
            : RawLanguage.Text;
        return new RawBody { Language = language, Content = joined };
    }

    private static string EncodePair(string value)
    {
        var eq = value.IndexOf('=');
        return eq > 0 ? value[..eq] + "=" + Uri.EscapeDataString(value[(eq + 1)..]) : Uri.EscapeDataString(value.TrimStart('='));
    }

    private static KeyValueItem Header(string text)
    {
        var colon = text.IndexOf(':');
        return colon > 0
            ? new KeyValueItem { Key = text[..colon].Trim(), Value = text[(colon + 1)..].Trim() }
            : new KeyValueItem { Key = text.TrimEnd(';').Trim(), Value = "" };
    }

    private static MultipartPart FormPart(string text, bool literal)
    {
        var eq = text.IndexOf('=');
        var name = eq >= 0 ? text[..eq] : text;
        var value = eq >= 0 ? text[(eq + 1)..] : "";
        string? type = null;
        var typeAt = value.IndexOf(";type=", StringComparison.OrdinalIgnoreCase);
        if (!literal && typeAt >= 0)
        {
            type = value[(typeAt + 6)..];
            value = value[..typeAt];
        }
        return !literal && value.StartsWith('@')
            ? new MultipartPart { Name = name, Kind = MultipartPartKind.File, Value = value[1..].Trim('"'), ContentType = type }
            : new MultipartPart { Name = name, Value = value, ContentType = type };
    }

    private static AuthConfig? AuthFromHeader(string value)
    {
        var space = value.IndexOf(' ');
        if (space < 0) return null;
        var (scheme, rest) = (value[..space], value[(space + 1)..].Trim());
        if (scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)) return new BearerAuth { Token = rest };
        if (scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(rest));
                var colon = decoded.IndexOf(':');
                if (colon >= 0) return new BasicAuth { Username = decoded[..colon], Password = decoded[(colon + 1)..] };
            }
            catch (FormatException)
            {
            }
        }
        return null;
    }

    private static string DefaultName(RequestDefinition r)
    {
        var path = UrlTools.Split(r.Url).BeforeQuery;
        var scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) path = path[(scheme + 3)..];
        var slash = path.IndexOf('/');
        path = slash >= 0 ? path[slash..] : "/";
        return $"{r.Method} {path}";
    }

    /// <summary>Splits a shell command into arguments: bash quoting and \ continuations, or cmd's ^ escapes.</summary>
    internal static List<string> Tokenize(string command)
    {
        var text = command.Replace("\r\n", "\n").Trim();
        if (text.Contains("^\"") || text.Contains("^\n"))
        {
            // cmd: ^X is a literal X and ^ at a line end continues it; the program then sees Windows quoting.
            text = Regex.Replace(text, @"\^\n", "");
            text = Regex.Replace(text, @"\^(.)", "$1", RegexOptions.Singleline);
            return TokenizeWindows(text);
        }
        return TokenizeBash(text);
    }

    private static List<string> TokenizeBash(string text)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inToken = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\\' or '`' && i + 1 < text.Length && text[i + 1] == '\n') // bash / PowerShell line continuation
            {
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (inToken) args.Add(current.ToString());
                current.Clear();
                inToken = false;
                continue;
            }

            inToken = true;
            if (c == '\'')
            {
                var end = text.IndexOf('\'', i + 1);
                if (end < 0) end = text.Length;
                current.Append(text, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '$' && i + 1 < text.Length && text[i + 1] == '\'')
            {
                // ANSI-C quoting: $'...' with \n, \t, \' and \\ escapes.
                for (i += 2; i < text.Length && text[i] != '\''; i++)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        var e = text[++i];
                        current.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e });
                    }
                    else current.Append(text[i]);
                }
            }
            else if (c == '"')
            {
                for (i++; i < text.Length && text[i] != '"'; i++)
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\' or '$' or '`') current.Append(text[++i]);
                    else current.Append(text[i]);
                }
            }
            else if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(text[++i]);
            }
            else
            {
                current.Append(c);
            }
        }
        if (inToken) args.Add(current.ToString());
        return args;
    }

    /// <summary>Windows (MSVC) argument rules: double quotes group, \" is a literal quote.</summary>
    private static List<string> TokenizeWindows(string text)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '"')
            {
                current.Append('"');
                i++;
                inToken = true;
            }
            else if (c == '"')
            {
                quoted = !quoted;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (inToken) args.Add(current.ToString());
                current.Clear();
                inToken = false;
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }
        if (inToken) args.Add(current.ToString());
        return args;
    }
}
