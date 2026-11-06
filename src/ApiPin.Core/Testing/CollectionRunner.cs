using System.Diagnostics;
using System.Net;
using ApiPin.Core.Execution;
using ApiPin.Core.Http;
using ApiPin.Core.Models;
using ApiPin.Core.Variables;

namespace ApiPin.Core.Testing;

public sealed record RunOptions
{
    public int Iterations { get; init; } = 1;
    public int DelayMs { get; init; }

    /// <summary>Stop at the first failed assertion, capture or request error.</summary>
    public bool StopOnFailure { get; init; }
}

/// <summary>One request to run, with the folders it inherits from.</summary>
public sealed record RunItem(RequestDefinition Request, IReadOnlyList<RequestFolder> Chain)
{
    /// <summary>"Collection / Folder / Request", for reports.</summary>
    public string Path => string.Join(" / ", Chain.Select(f => f.Name).Append(Name));
    public string Name => string.IsNullOrWhiteSpace(Request.Name) ? Request.Url : Request.Name;
}

public sealed record RunStep(int Iteration, RunItem Item, ResponseResult Response, TestReport Tests, ResolveReport Variables)
{
    public bool Passed => !Response.IsError && Tests.AllPassed;
}

public sealed record RunResult(IReadOnlyList<RunStep> Steps, TimeSpan Duration, bool Cancelled, bool Stopped)
{
    public int Requests => Steps.Count;
    public int FailedRequests => Steps.Count(s => !s.Passed);
    public int Assertions => Steps.Sum(s => s.Tests.Assertions.Count);
    public int FailedAssertions => Steps.Sum(s => s.Tests.Assertions.Count(a => !a.Passed));
    public bool AllPassed => !Cancelled && Steps.Count > 0 && Steps.All(s => s.Passed);
}

/// <summary>
/// Variables a run uses and changes: runtime values (captures, overrides from the command line), the environment
/// and the globals. Captured values are visible to the requests that follow.
/// </summary>
public sealed class RunVariables
{
    public Dictionary<string, string> Runtime { get; } = new(StringComparer.Ordinal);
    public EnvironmentDefinition? Environment { get; init; }
    public List<Variable> Globals { get; init; } = [];
    public CookieContainer Cookies { get; init; } = new();

    /// <summary>Called when a capture targets the environment (the UI saves it; the CLI doesn't).</summary>
    public Action<EnvironmentDefinition>? EnvironmentChanged { get; init; }

    public VariableContext Context()
    {
        var context = VariableContext.Empty.With(VariableSource.Runtime, "runtime", Runtime);
        if (Environment is not null) context = context.With(VariableSource.Environment, Environment.Name, Environment.Variables);
        return context.With(VariableSource.Global, "Globals", Globals);
    }

    /// <summary>Stores successful captures.</summary>
    public void Apply(IEnumerable<CaptureResult> captures)
    {
        var envChanged = false;
        foreach (var c in captures.Where(c => c.Succeeded))
        {
            var name = c.Rule.Variable.Trim();
            if (c.Rule.Target == CaptureTarget.Environment && Environment is not null)
            {
                var existing = Environment.Variables.FirstOrDefault(v => v.Key.Trim() == name);
                if (existing is null) Environment.Variables.Add(new Variable { Key = name, Value = c.Value! });
                else existing.Value = c.Value!;
                Runtime.Remove(name); // a runtime value would hide the environment one
                envChanged = true;
            }
            else
            {
                Runtime[name] = c.Value!;
            }
        }
        if (envChanged && Environment is not null) EnvironmentChanged?.Invoke(Environment);
    }
}

/// <summary>Runs a folder or collection in order: prepare, send, capture, assert, for each request and iteration.</summary>
public sealed class CollectionRunner(IHttpExecutor executor)
{
    /// <summary>The requests in a folder in sidebar order (sub-folders first, then requests), each with its inheritance chain.</summary>
    /// <param name="above">The folders above <paramref name="folder"/>, collection first; empty when it is the collection.</param>
    public static List<RunItem> Plan(RequestFolder folder, IReadOnlyList<RequestFolder>? above = null)
    {
        var items = new List<RunItem>();
        Add(folder, [.. above ?? [], folder]);
        return items;

        void Add(RequestFolder f, List<RequestFolder> chain)
        {
            foreach (var sub in f.OrderedFolders) Add(sub, [.. chain, sub]);
            foreach (var r in f.OrderedRequests) items.Add(new RunItem(r, chain));
        }
    }

    public async Task<RunResult> RunAsync(IReadOnlyList<RunItem> items, RunVariables variables, RunOptions options,
        IProgress<RunStep>? progress = null, CancellationToken cancellationToken = default)
    {
        var steps = new List<RunStep>();
        var clock = Stopwatch.StartNew();
        try
        {
            for (var iteration = 1; iteration <= Math.Max(1, options.Iterations); iteration++)
            {
                variables.Runtime["$iteration"] = iteration.ToString();
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (steps.Count > 0 && options.DelayMs > 0) await Task.Delay(options.DelayMs, cancellationToken);

                    var prepared = RequestPreparer.Prepare(item.Request, item.Chain, variables.Context());
                    var response = await executor.SendAsync(prepared.Request, variables.Cookies, cancellationToken);
                    if (response.Error?.Kind == ErrorKind.Cancelled) return new RunResult(steps, clock.Elapsed, Cancelled: true, Stopped: false);

                    var tests = ResponseTester.Run(item.Request, response, prepared.Variables);
                    variables.Apply(tests.Captures);

                    var step = new RunStep(iteration, item, response, tests, prepared.Report);
                    steps.Add(step);
                    progress?.Report(step);
                    if (options.StopOnFailure && !step.Passed) return new RunResult(steps, clock.Elapsed, Cancelled: false, Stopped: true);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RunResult(steps, clock.Elapsed, Cancelled: true, Stopped: false);
        }
        return new RunResult(steps, clock.Elapsed, Cancelled: false, Stopped: false);
    }
}
