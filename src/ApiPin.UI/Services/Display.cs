namespace ApiPin.UI.Services;

/// <summary>Small formatting helpers shared by components.</summary>
public static class Display
{
    public static readonly string[] StandardMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public static readonly string[] CommonHeaders =
    [
        "Accept", "Accept-Encoding", "Accept-Language", "Authorization", "Cache-Control", "Connection",
        "Content-Type", "Cookie", "Host", "If-Match", "If-None-Match", "If-Modified-Since", "Origin",
        "Referer", "User-Agent", "X-Api-Key", "X-Correlation-Id", "X-Forwarded-For", "X-Request-Id",
    ];

    public static string MethodClass(string method) => method.Trim().ToUpperInvariant() switch
    {
        "GET" => "m-get",
        "POST" => "m-post",
        "PUT" => "m-put",
        "PATCH" => "m-patch",
        "DELETE" => "m-delete",
        _ => "m-other",
    };

    public static string ShortMethod(string method)
    {
        method = method.Trim().ToUpperInvariant();
        return method switch
        {
            "" => "GET",
            "DELETE" => "DEL",
            "OPTIONS" => "OPT",
            _ => method.Length > 5 ? method[..5] : method,
        };
    }

    public static string Duration(TimeSpan t) =>
        t.TotalMilliseconds < 1000 ? $"{t.TotalMilliseconds:0} ms" : $"{t.TotalSeconds:0.00} s";

    public static string StatusClass(int? status) => status switch
    {
        >= 200 and < 300 => "s-ok",
        >= 300 and < 400 => "s-redirect",
        >= 400 and < 500 => "s-client",
        >= 500 => "s-server",
        _ => "s-info",
    };
}
