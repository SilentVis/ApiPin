using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ApiPin.Core.Testing;

/// <summary>Writes a run as JUnit XML (for CI test tabs), JSON, or readable console text.</summary>
public static class RunReporters
{
    public static string JUnit(RunResult result, string suiteName)
    {
        var cases = result.Steps.Select(step =>
        {
            var element = new XElement("testcase",
                new XAttribute("classname", string.Join(".", step.Item.Chain.Select(f => f.Name))),
                new XAttribute("name", Name(step)),
                new XAttribute("time", Seconds(step.Response.Timings.Total)));
            if (step.Response.Error is { } error)
                element.Add(new XElement("error", new XAttribute("message", error.Title), error.Detail));
            foreach (var a in step.Tests.Assertions.Where(a => !a.Passed))
                element.Add(new XElement("failure", new XAttribute("message", a.Description), a.Message ?? ""));
            foreach (var c in step.Tests.Captures.Where(c => !c.Succeeded))
                element.Add(new XElement("failure", new XAttribute("message", $"Capture {{{{{c.Rule.Variable}}}}}"), c.Error ?? ""));
            var passed = step.Tests.Assertions.Where(a => a.Passed).Select(a => "✓ " + a.Description);
            element.Add(new XElement("system-out", $"{step.Response.Request?.Method} {step.Response.Request?.Url} → {Status(step)}\n{string.Join("\n", passed)}"));
            return element;
        }).ToList();

        var suite = new XElement("testsuite",
            new XAttribute("name", suiteName),
            new XAttribute("tests", result.Steps.Count),
            new XAttribute("failures", result.Steps.Count(s => !s.Response.IsError && !s.Tests.AllPassed)),
            new XAttribute("errors", result.Steps.Count(s => s.Response.IsError)),
            new XAttribute("time", Seconds(result.Duration)),
            new XAttribute("timestamp", DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture)),
            cases);
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("testsuites", suite));
        return document.Declaration + Environment.NewLine + document; // ToString() alone drops the declaration
    }

    public static string Json(RunResult result) => JsonSerializer.Serialize(new
    {
        passed = result.AllPassed,
        cancelled = result.Cancelled,
        stopped = result.Stopped,
        durationMs = (int)result.Duration.TotalMilliseconds,
        requests = result.Requests,
        failedRequests = result.FailedRequests,
        assertions = result.Assertions,
        failedAssertions = result.FailedAssertions,
        steps = result.Steps.Select(s => new
        {
            iteration = s.Iteration,
            request = s.Item.Path,
            method = s.Response.Request?.Method,
            url = s.Response.Request?.Url,
            status = s.Response.StatusCode,
            error = s.Response.Error?.Title,
            timeMs = (int)s.Response.Timings.Total.TotalMilliseconds,
            passed = s.Passed,
            assertions = s.Tests.Assertions.Select(a => new { name = a.Description, passed = a.Passed, message = a.Message }),
            captures = s.Tests.Captures.Select(c => new { variable = c.Rule.Variable, captured = c.Succeeded, error = c.Error }),
            undefinedVariables = s.Variables.Unresolved,
        }),
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>One line per request, indented checks, and a summary.</summary>
    public static string Console(RunResult result, bool showPassedChecks = true)
    {
        var sb = new StringBuilder();
        var iterations = result.Steps.Select(s => s.Iteration).Distinct().Count() > 1;
        foreach (var step in result.Steps) sb.Append(ConsoleStep(step, showPassedChecks, iterations));
        sb.AppendLine();
        sb.Append($"{result.Requests} requests ({result.FailedRequests} failed), {result.Assertions} assertions ({result.FailedAssertions} failed) in {result.Duration.TotalSeconds:0.00} s");
        if (result.Cancelled) sb.Append(" — cancelled");
        if (result.Stopped) sb.Append(" — stopped at the first failure");
        return sb.ToString();
    }

    /// <summary>A request's line and its checks, for printing as the run goes.</summary>
    public static string ConsoleStep(RunStep step, bool showPassedChecks = true, bool showIteration = false)
    {
        var sb = new StringBuilder();
        var mark = step.Passed ? "✓" : "✗";
        var iteration = showIteration ? $"[{step.Iteration}] " : "";
        sb.AppendLine($"{mark} {iteration}{step.Item.Path}  {step.Response.Request?.Method} {step.Response.Request?.Url} → {Status(step)} ({(int)step.Response.Timings.Total.TotalMilliseconds} ms)");
        if (step.Response.Error is { } error) sb.AppendLine($"    ✗ {error.Title}: {error.Detail}");
        foreach (var a in step.Tests.Assertions.Where(a => showPassedChecks || !a.Passed))
            sb.AppendLine(a.Passed ? $"    ✓ {a.Description}" : $"    ✗ {a.Description}: {a.Message}");
        foreach (var c in step.Tests.Captures.Where(c => showPassedChecks || !c.Succeeded))
            sb.AppendLine(c.Succeeded ? $"    → {{{{{c.Rule.Variable}}}}} captured" : $"    ✗ capture {{{{{c.Rule.Variable}}}}}: {c.Error}");
        if (step.Variables.Unresolved.Count > 0) sb.AppendLine($"    ! undefined: {string.Join(", ", step.Variables.Unresolved)}");
        return sb.ToString();
    }

    private static string Name(RunStep step) => step.Iteration > 1 ? $"{step.Item.Name} [{step.Iteration}]" : step.Item.Name;

    private static string Status(RunStep step) => step.Response.Error is { } e ? e.Title : $"{step.Response.StatusCode} {step.Response.ReasonPhrase}";

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
}
