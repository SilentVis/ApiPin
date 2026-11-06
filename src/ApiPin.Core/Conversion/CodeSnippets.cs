using System.Text;
using System.Text.Json;
using ApiPin.Core.Http;
using ApiPin.Core.Models;

namespace ApiPin.Core.Conversion;

public enum SnippetLanguage { Curl, CurlCmd, PowerShell, CSharp, JavaScript, Python }

/// <summary>
/// Code that sends the same request from a terminal or another program. Works on a request whose variables are
/// resolved and whose auth is one the builder understands (token auth already turned into a bearer token).
/// </summary>
public static class CodeSnippets
{
    public static string LanguageName(SnippetLanguage language) => language switch
    {
        SnippetLanguage.Curl => "cURL (bash)",
        SnippetLanguage.CurlCmd => "cURL (cmd)",
        SnippetLanguage.PowerShell => "PowerShell",
        SnippetLanguage.CSharp => "C# HttpClient",
        SnippetLanguage.JavaScript => "JavaScript fetch",
        _ => "Python requests",
    };

    /// <exception cref="RequestBuildException">The request can't be built (bad URL, missing file…).</exception>
    public static string Generate(RequestDefinition request, SnippetLanguage language)
    {
        var parts = Describe(request);
        var code = language switch
        {
            SnippetLanguage.Curl => Curl(parts, cmd: false),
            SnippetLanguage.CurlCmd => Curl(parts, cmd: true),
            SnippetLanguage.PowerShell => PowerShell(parts),
            SnippetLanguage.CSharp => CSharp(parts),
            SnippetLanguage.JavaScript => JavaScript(parts),
            _ => Python(parts),
        };
        return code.ReplaceLineEndings("\n");
    }

    private sealed record Parts(string Method, string Url, List<KeyValuePair<string, string>> Headers, BodyConfig Body, bool Insecure, bool FollowRedirects);

    private static Parts Describe(RequestDefinition request)
    {
        using var message = HttpRequestBuilder.Build(request);
        var headers = message.Headers.NonValidated
            .Where(h => h.Key is not "User-Agent" || !h.Value.ToString().StartsWith("ApiPin", StringComparison.Ordinal))
            .Where(h => !(h.Key == "Accept" && h.Value.ToString() == "*/*"))
            .SelectMany(h => h.Value.Select(v => KeyValuePair.Create(h.Key, v)))
            .ToList();
        if (message.Content is { } content && request.Body is not (MultipartBody or FormUrlEncodedBody))
        {
            headers.AddRange(content.Headers.NonValidated.Where(h => h.Key is not "Content-Length")
                .SelectMany(h => h.Value.Select(v => KeyValuePair.Create(h.Key, v.Replace("; charset=utf-8", "")))));
        }
        return new Parts(message.Method.Method, message.RequestUri!.AbsoluteUri, headers, request.Body, !request.Settings.VerifyTls, request.Settings.FollowRedirects);
    }

    private static string BodyText(BodyConfig body) => body switch
    {
        RawBody raw => raw.Content,
        GraphQlBody graphql => HttpRequestBuilder.GraphQlJson(graphql),
        _ => "",
    };

    private static List<KeyValueItem> Fields(BodyConfig body) =>
        body is FormUrlEncodedBody form ? form.Fields.Where(f => f.IsActive).ToList() : [];

    private static List<MultipartPart> PartsOf(BodyConfig body) =>
        body is MultipartBody multipart ? multipart.Parts.Where(p => p.Enabled && p.Name.Length > 0).ToList() : [];

    // ---------- cURL ----------

    private static string Curl(Parts p, bool cmd)
    {
        string Q(string s) => cmd ? CmdQuote(s) : BashQuote(s);
        var lines = new List<string> { $"curl{(p.Method == "GET" ? "" : " -X " + p.Method)} {Q(p.Url)}" };
        if (p.FollowRedirects) lines.Add("-L");
        if (p.Insecure) lines.Add("-k");
        lines.AddRange(p.Headers.Select(h => $"-H {Q($"{h.Key}: {h.Value}")}"));
        switch (p.Body)
        {
            case RawBody or GraphQlBody:
                lines.Add($"--data-raw {Q(BodyText(p.Body))}");
                break;
            case FormUrlEncodedBody:
                lines.AddRange(Fields(p.Body).Select(f => $"--data-urlencode {Q($"{f.Key}={f.Value}")}"));
                break;
            case MultipartBody:
                lines.AddRange(PartsOf(p.Body).Select(part => part.Kind == MultipartPartKind.File
                    ? $"-F {Q($"{part.Name}=@{part.Value}{(part.ContentType is { Length: > 0 } t ? ";type=" + t : "")}")}"
                    : $"-F {Q($"{part.Name}={part.Value}")}"));
                break;
            case BinaryFileBody binary:
                lines.Add($"--data-binary {Q("@" + binary.FilePath)}");
                break;
        }
        return string.Join(cmd ? " ^\n  " : " \\\n  ", lines);
    }

    private static string BashQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string CmdQuote(string s) => "\"" + s.Replace("\"", "\\\"").Replace("%", "%%") + "\"";

    // ---------- PowerShell ----------

    private static string PowerShell(Parts p)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var sb = new StringBuilder();
        var headers = p.Headers.Where(h => h.Key != "Content-Type").ToList();
        var contentType = p.Headers.FirstOrDefault(h => h.Key == "Content-Type").Value;
        if (headers.Count > 0)
        {
            sb.AppendLine("$headers = @{");
            foreach (var h in headers) sb.AppendLine($"    {Q(h.Key)} = {Q(h.Value)}");
            sb.AppendLine("}");
        }

        var args = new List<string> { $"-Uri {Q(p.Url)}", $"-Method {p.Method}" };
        if (headers.Count > 0) args.Add("-Headers $headers");
        switch (p.Body)
        {
            case RawBody or GraphQlBody:
                sb.AppendLine($"$body = @'\n{BodyText(p.Body)}\n'@");
                args.Add("-Body $body");
                if (contentType is not null) args.Add($"-ContentType {Q(contentType)}");
                break;
            case FormUrlEncodedBody:
                sb.AppendLine("$body = @{");
                foreach (var f in Fields(p.Body)) sb.AppendLine($"    {Q(f.Key)} = {Q(f.Value)}");
                sb.AppendLine("}");
                args.Add("-Body $body");
                break;
            case MultipartBody:
                sb.AppendLine("$form = @{");
                foreach (var part in PartsOf(p.Body))
                    sb.AppendLine($"    {Q(part.Name)} = {(part.Kind == MultipartPartKind.File ? $"Get-Item -Path {Q(part.Value)}" : Q(part.Value))}");
                sb.AppendLine("}");
                args.Add("-Form $form");
                break;
            case BinaryFileBody binary:
                args.Add($"-InFile {Q(binary.FilePath)}");
                if (contentType is not null) args.Add($"-ContentType {Q(contentType)}");
                break;
        }
        if (p.Insecure) args.Add("-SkipCertificateCheck");
        if (!p.FollowRedirects) args.Add("-MaximumRedirection 0");
        sb.Append("Invoke-RestMethod ").Append(string.Join(" `\n    ", args));
        return sb.ToString();
    }

    // ---------- C# ----------

    private static string CSharp(Parts p)
    {
        static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        var sb = new StringBuilder();
        if (p.Insecure || !p.FollowRedirects)
        {
            sb.AppendLine("using var handler = new HttpClientHandler");
            sb.AppendLine("{");
            if (p.Insecure) sb.AppendLine("    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,");
            if (!p.FollowRedirects) sb.AppendLine("    AllowAutoRedirect = false,");
            sb.AppendLine("};");
            sb.AppendLine("using var client = new HttpClient(handler);");
        }
        else
        {
            sb.AppendLine("using var client = new HttpClient();");
        }
        var method = p.Method switch { "GET" => "HttpMethod.Get", "POST" => "HttpMethod.Post", "PUT" => "HttpMethod.Put", "PATCH" => "HttpMethod.Patch", "DELETE" => "HttpMethod.Delete", "HEAD" => "HttpMethod.Head", "OPTIONS" => "HttpMethod.Options", _ => $"new HttpMethod({Q(p.Method)})" };
        sb.AppendLine($"using var request = new HttpRequestMessage({method}, {Q(p.Url)});");
        var contentType = p.Headers.FirstOrDefault(h => h.Key == "Content-Type").Value ?? "text/plain";
        foreach (var h in p.Headers.Where(h => h.Key != "Content-Type"))
            sb.AppendLine($"request.Headers.TryAddWithoutValidation({Q(h.Key)}, {Q(h.Value)});");

        switch (p.Body)
        {
            case RawBody or GraphQlBody:
                sb.AppendLine($"request.Content = new StringContent({Q(BodyText(p.Body))}, System.Text.Encoding.UTF8, {Q(contentType.Split(';')[0])});");
                break;
            case FormUrlEncodedBody:
                sb.AppendLine("request.Content = new FormUrlEncodedContent(new Dictionary<string, string>");
                sb.AppendLine("{");
                foreach (var f in Fields(p.Body)) sb.AppendLine($"    [{Q(f.Key)}] = {Q(f.Value)},");
                sb.AppendLine("});");
                break;
            case MultipartBody:
                sb.AppendLine("var form = new MultipartFormDataContent();");
                foreach (var part in PartsOf(p.Body))
                {
                    sb.AppendLine(part.Kind == MultipartPartKind.File
                        ? $"form.Add(new StreamContent(File.OpenRead({Q(part.Value)})), {Q(part.Name)}, {Q(Path.GetFileName(part.Value))});"
                        : $"form.Add(new StringContent({Q(part.Value)}), {Q(part.Name)});");
                }
                sb.AppendLine("request.Content = form;");
                break;
            case BinaryFileBody binary:
                sb.AppendLine($"request.Content = new StreamContent(File.OpenRead({Q(binary.FilePath)}));");
                sb.AppendLine($"request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue({Q(contentType.Split(';')[0])});");
                break;
        }
        sb.AppendLine();
        sb.AppendLine("using var response = await client.SendAsync(request);");
        sb.AppendLine("Console.WriteLine($\"{(int)response.StatusCode} {response.ReasonPhrase}\");");
        sb.Append("Console.WriteLine(await response.Content.ReadAsStringAsync());");
        return sb.ToString();
    }

    // ---------- JavaScript ----------

    private static string JavaScript(Parts p)
    {
        static string Q(string s) => JsonSerializer.Serialize(s, Relaxed);
        var sb = new StringBuilder();
        var bodyLine = (string?)null;
        switch (p.Body)
        {
            case RawBody or GraphQlBody:
                bodyLine = $"body: {Q(BodyText(p.Body))},";
                break;
            case FormUrlEncodedBody:
                sb.AppendLine("const body = new URLSearchParams();");
                foreach (var f in Fields(p.Body)) sb.AppendLine($"body.append({Q(f.Key)}, {Q(f.Value)});");
                bodyLine = "body,";
                break;
            case MultipartBody:
                sb.AppendLine("const body = new FormData();");
                foreach (var part in PartsOf(p.Body))
                {
                    sb.AppendLine(part.Kind == MultipartPartKind.File
                        ? $"body.append({Q(part.Name)}, fileInput.files[0]); // {part.Value}"
                        : $"body.append({Q(part.Name)}, {Q(part.Value)});");
                }
                bodyLine = "body,";
                break;
            case BinaryFileBody binary:
                bodyLine = $"body: file, // {binary.FilePath}";
                break;
        }

        sb.AppendLine($"const response = await fetch({Q(p.Url)}, {{");
        sb.AppendLine($"  method: {Q(p.Method)},");
        if (p.Headers.Count > 0)
        {
            sb.AppendLine("  headers: {");
            foreach (var h in p.Headers) sb.AppendLine($"    {Q(h.Key)}: {Q(h.Value)},");
            sb.AppendLine("  },");
        }
        if (bodyLine is not null) sb.AppendLine("  " + bodyLine);
        if (!p.FollowRedirects) sb.AppendLine("  redirect: \"manual\",");
        sb.AppendLine("});");
        sb.Append("console.log(response.status, await response.text());");
        return sb.ToString();
    }

    // ---------- Python ----------

    private static string Python(Parts p)
    {
        static string Q(string s) => JsonSerializer.Serialize(s, Relaxed);
        var sb = new StringBuilder("import requests\n\n");
        var args = new List<string> { Q(p.Method), Q(p.Url) };
        if (p.Headers.Count > 0)
        {
            sb.AppendLine("headers = {");
            foreach (var h in p.Headers.Where(h => !(p.Body is MultipartBody && h.Key == "Content-Type"))) sb.AppendLine($"    {Q(h.Key)}: {Q(h.Value)},");
            sb.AppendLine("}");
            args.Add("headers=headers");
        }
        switch (p.Body)
        {
            case RawBody or GraphQlBody:
                sb.AppendLine($"data = {Q(BodyText(p.Body))}");
                args.Add("data=data.encode(\"utf-8\")");
                break;
            case FormUrlEncodedBody:
                sb.AppendLine("data = {");
                foreach (var f in Fields(p.Body)) sb.AppendLine($"    {Q(f.Key)}: {Q(f.Value)},");
                sb.AppendLine("}");
                args.Add("data=data");
                break;
            case MultipartBody:
                sb.AppendLine("files = {");
                foreach (var part in PartsOf(p.Body))
                    sb.AppendLine(part.Kind == MultipartPartKind.File
                        ? $"    {Q(part.Name)}: open({Q(part.Value)}, \"rb\"),"
                        : $"    {Q(part.Name)}: (None, {Q(part.Value)}),");
                sb.AppendLine("}");
                args.Add("files=files");
                break;
            case BinaryFileBody binary:
                args.Add($"data=open({Q(binary.FilePath)}, \"rb\")");
                break;
        }
        if (p.Insecure) args.Add("verify=False");
        if (!p.FollowRedirects) args.Add("allow_redirects=False");
        sb.AppendLine();
        sb.AppendLine($"response = requests.request({string.Join(", ", args)})");
        sb.Append("print(response.status_code, response.text)");
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
