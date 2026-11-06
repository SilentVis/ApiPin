namespace ApiPin.Core.Models;

public enum ProxyMode { System, None, Custom }

/// <summary>App-wide proxy for outgoing requests (and token requests).</summary>
public sealed record ProxySettings
{
    public ProxyMode Mode { get; init; } = ProxyMode.System;

    /// <summary>Custom proxy, e.g. <c>http://proxy.corp:8080</c>.</summary>
    public string Url { get; init; } = "";

    /// <summary>Hosts that skip the proxy, separated by commas, semicolons or spaces; <c>*.corp.local</c> wildcards work.</summary>
    public string Bypass { get; init; } = "";

    /// <summary>Go direct to localhost and other local addresses.</summary>
    public bool BypassLocal { get; init; } = true;

    public string Username { get; init; } = "";
    public string Password { get; init; } = "";

    /// <summary>The bypass entries one by one.</summary>
    public IEnumerable<string> BypassEntries =>
        Bypass.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
