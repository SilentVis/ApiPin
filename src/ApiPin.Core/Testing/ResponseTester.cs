using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApiPin.Core.Formatting;
using ApiPin.Core.Models;
using ApiPin.Core.Variables;

namespace ApiPin.Core.Testing;

public sealed record AssertionResult(Assertion Assertion, bool Passed, string Description, string? Actual, string? Message);

public sealed record CaptureResult(CaptureRule Rule, string? Value, string? Error)
{
    public bool Succeeded => Error is null && Value is not null;
}

/// <summary>Results of a response's captures and assertions.</summary>
public sealed record TestReport(IReadOnlyList<AssertionResult> Assertions, IReadOnlyList<CaptureResult> Captures)
{
    public static readonly TestReport Empty = new([], []);

    public int Passed => Assertions.Count(a => a.Passed);
    public int Failed => Assertions.Count(a => !a.Passed) + Captures.Count(c => !c.Succeeded);
    public bool AllPassed => Failed == 0;
    public bool HasAny => Assertions.Count > 0 || Captures.Count > 0;
}

/// <summary>Runs a request's captures and assertions against its response.</summary>
public static class ResponseTester
{
    public static TestReport Run(RequestDefinition request, ResponseResult response, VariableContext? variables = null)
    {
        var body = new Lazy<JsonElement?>(() => ParseJson(response));
        var captures = request.Captures.Where(c => c.Enabled && c.Variable.Trim().Length > 0).Select(c => Capture(c, response, body)).ToList();
        var assertions = request.Assertions.Where(a => a.Enabled).Select(a => Check(a, response, body, variables ?? VariableContext.Empty)).ToList();
        return new TestReport(assertions, captures);
    }

    // ---------- Captures ----------

    public static CaptureResult Capture(CaptureRule rule, ResponseResult response, Lazy<JsonElement?> body)
    {
        if (response.IsError) return new(rule, null, "No response.");
        try
        {
            switch (rule.Source)
            {
                case CaptureSource.Status:
                    return new(rule, response.StatusCode?.ToString(CultureInfo.InvariantCulture), null);

                case CaptureSource.Header:
                    var header = response.Headers.FirstOrDefault(h => h.Key.Equals(rule.Expression.Trim(), StringComparison.OrdinalIgnoreCase));
                    return header.Key is null ? new(rule, null, $"No '{rule.Expression.Trim()}' header.") : new(rule, header.Value, null);

                case CaptureSource.Regex:
                    var match = Regex.Match(Text(response), rule.Expression, RegexOptions.None, TimeSpan.FromSeconds(2));
                    return match.Success
                        ? new(rule, match.Groups.Count > 1 ? match.Groups[1].Value : match.Value, null)
                        : new(rule, null, "The pattern doesn't match the body.");

                default:
                    if (body.Value is not { } json) return new(rule, null, "The body isn't JSON.");
                    var found = JsonPath.Select(json, rule.Expression);
                    return found.Count == 0 ? new(rule, null, $"Nothing at {rule.Expression}.") : new(rule, JsonPath.Text(found[0]), null);
            }
        }
        catch (Exception ex) when (ex is JsonPathException or ArgumentException or RegexMatchTimeoutException)
        {
            return new(rule, null, ex.Message);
        }
    }

    // ---------- Assertions ----------

    public static AssertionResult Check(Assertion a, ResponseResult response, Lazy<JsonElement?> body, VariableContext variables)
    {
        var expected = VariableResolver.Resolve(a.Expected, variables).Trim();
        var description = Describe(a, expected);
        if (response.IsError) return new(a, false, description, null, response.Error!.Title);

        try
        {
            switch (a.Kind)
            {
                case AssertionKind.Status:
                    return Compare(a, description, response.StatusCode?.ToString(CultureInfo.InvariantCulture), expected);

                case AssertionKind.ResponseTime:
                    var ms = response.Timings.Total.TotalMilliseconds;
                    return Compare(a, description, ms.ToString("0", CultureInfo.InvariantCulture), expected);

                case AssertionKind.Header:
                    var values = response.Headers.Where(h => h.Key.Equals(a.Target.Trim(), StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
                    return Compare(a, description, values.Count == 0 ? null : string.Join(", ", values), expected);

                case AssertionKind.Body:
                    return Compare(a, description, Text(response), expected);

                case AssertionKind.JsonPath:
                    if (body.Value is not { } json) return new(a, false, description, null, "The body isn't JSON.");
                    var found = JsonPath.Select(json, a.Target);
                    return Compare(a, description, found.Count == 0 ? null : found.Count == 1 ? JsonPath.Text(found[0]) : JsonSerializer.Serialize(found), expected);

                case AssertionKind.JsonSchema:
                    if (body.Value is not { } doc) return new(a, false, description, null, "The body isn't JSON.");
                    JsonElement schema;
                    try { schema = JsonDocument.Parse(expected).RootElement; }
                    catch (JsonException ex) { return new(a, false, description, null, $"The schema isn't valid JSON: {ex.Message}"); }
                    var target = a.Target.Trim().Length > 0 ? JsonPath.Select(doc, a.Target).FirstOrDefault() : doc;
                    var errors = JsonSchemaValidator.Validate(target, schema);
                    return new(a, errors.Count == 0, description, null, errors.Count == 0 ? null : string.Join(" ", errors.Take(5)) + (errors.Count > 5 ? $" (+{errors.Count - 5} more)" : ""));
            }
        }
        catch (Exception ex) when (ex is JsonPathException or ArgumentException or RegexMatchTimeoutException)
        {
            return new(a, false, description, null, ex.Message);
        }
        return new(a, false, description, null, "Unknown assertion.");
    }

    private static AssertionResult Compare(Assertion a, string description, string? actual, string expected)
    {
        bool passed;
        string? message = null;
        switch (a.Operator)
        {
            case AssertionOperator.Exists: passed = actual is not null; break;
            case AssertionOperator.NotExists: passed = actual is null; break;
            case AssertionOperator.Equals: passed = actual is not null && Same(actual, expected); break;
            case AssertionOperator.NotEquals: passed = actual is null || !Same(actual, expected); break;
            case AssertionOperator.Contains: passed = actual?.Contains(expected, StringComparison.Ordinal) == true; break;
            case AssertionOperator.NotContains: passed = actual is null || !actual.Contains(expected, StringComparison.Ordinal); break;
            case AssertionOperator.Matches:
                passed = actual is not null && Regex.IsMatch(actual, expected, RegexOptions.None, TimeSpan.FromSeconds(2));
                break;
            case AssertionOperator.LessThan or AssertionOperator.GreaterThan:
                if (!TryNumber(actual, out var x) || !TryNumber(expected, out var y))
                {
                    passed = false;
                    message = "Not a number.";
                }
                else passed = a.Operator == AssertionOperator.LessThan ? x < y : x > y;
                break;
            case AssertionOperator.Between:
                var parts = expected.Split(['-', '–', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !TryNumber(parts[0], out var low) || !TryNumber(parts[1], out var high) || !TryNumber(actual, out var v))
                {
                    passed = false;
                    message = "Use \"low-high\", e.g. 200-299.";
                }
                else passed = v >= low && v <= high;
                break;
            default: passed = false; break;
        }
        if (!passed && message is null)
            message = actual is null ? "Not found." : $"Got {Truncate(actual)}.";
        return new(a, passed, description, actual, passed ? null : message);
    }

    /// <summary>Equal as text, or as numbers ("200" = "200.0"), or as JSON ("[1,2]" = "[1, 2]").</summary>
    private static bool Same(string actual, string expected)
    {
        if (actual == expected) return true;
        if (TryNumber(actual, out var a) && TryNumber(expected, out var b)) return a == b;
        try
        {
            using var x = JsonDocument.Parse(actual);
            using var y = JsonDocument.Parse(expected);
            return JsonElement.DeepEquals(x.RootElement, y.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static string Describe(Assertion a, string expected)
    {
        var subject = a.Kind switch
        {
            AssertionKind.Status => "Status",
            AssertionKind.ResponseTime => "Response time (ms)",
            AssertionKind.Header => $"Header {a.Target.Trim()}",
            AssertionKind.Body => "Body",
            AssertionKind.JsonPath => a.Target.Trim(),
            AssertionKind.JsonSchema => a.Target.Trim().Length > 0 ? $"{a.Target.Trim()} matches the schema" : "Body matches the schema",
            _ => a.Kind.ToString(),
        };
        if (a.Kind == AssertionKind.JsonSchema) return subject;
        var op = a.Operator switch
        {
            AssertionOperator.Equals => "=",
            AssertionOperator.NotEquals => "≠",
            AssertionOperator.Contains => "contains",
            AssertionOperator.NotContains => "doesn't contain",
            AssertionOperator.Exists => "exists",
            AssertionOperator.NotExists => "doesn't exist",
            AssertionOperator.LessThan => "<",
            AssertionOperator.GreaterThan => ">",
            AssertionOperator.Matches => "matches",
            AssertionOperator.Between => "between",
            _ => a.Operator.ToString(),
        };
        return a.Operator is AssertionOperator.Exists or AssertionOperator.NotExists ? $"{subject} {op}" : $"{subject} {op} {expected}";
    }

    private static JsonElement? ParseJson(ResponseResult response)
    {
        if (response.Body.Length == 0) return null;
        try
        {
            return JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(ResponseResult response) => BodyFormatter.Decode(response.Body, response.ContentType);

    private static string Truncate(string s) => s.Length > 120 ? s[..120] + "…" : s;
}
