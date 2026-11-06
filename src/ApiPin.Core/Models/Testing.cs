namespace ApiPin.Core.Models;

public enum CaptureSource { JsonPath, Header, Regex, Status }

/// <summary>Where a captured value goes: runtime (until the app closes or the run ends) or the active environment.</summary>
public enum CaptureTarget { Runtime, Environment }

/// <summary>After a response, sets a variable from it, e.g. <c>$.accessToken</c> → <c>{{token}}</c>.</summary>
public sealed class CaptureRule
{
    public bool Enabled { get; set; } = true;
    public string Variable { get; set; } = "";
    public CaptureSource Source { get; set; } = CaptureSource.JsonPath;

    /// <summary>A JSONPath, a header name, or a regex (its first group, or the whole match).</summary>
    public string Expression { get; set; } = "";

    public CaptureTarget Target { get; set; } = CaptureTarget.Runtime;

    public CaptureRule Clone() => (CaptureRule)MemberwiseClone();
}

public enum AssertionKind { Status, Header, JsonPath, ResponseTime, Body, JsonSchema }

public enum AssertionOperator { Equals, NotEquals, Contains, NotContains, Exists, NotExists, LessThan, GreaterThan, Matches, Between }

/// <summary>A declarative check on a response, e.g. status equals 200, <c>$.items</c> exists, time &lt; 500 ms.</summary>
public sealed class Assertion
{
    public bool Enabled { get; set; } = true;
    public AssertionKind Kind { get; set; } = AssertionKind.Status;

    /// <summary>The header name or JSONPath, for those kinds.</summary>
    public string Target { get; set; } = "";

    public AssertionOperator Operator { get; set; } = AssertionOperator.Equals;

    /// <summary>The expected value; may use {{variables}}. "200-299" for Between; a JSON Schema for JsonSchema.</summary>
    public string Expected { get; set; } = "";

    public Assertion Clone() => (Assertion)MemberwiseClone();
}
