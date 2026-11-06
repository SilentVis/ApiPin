using ApiPin.Core.Auth;
using ApiPin.Core.Http;
using ApiPin.Core.Models;
using ApiPin.Core.Testing;
using ApiPin.Storage;

namespace ApiPin.Cli;

/// <summary>The <c>apipin</c> command line; separate from Program so tests can run it in-process.</summary>
public static class CliApp
{
    public const int ExitPassed = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const string Usage = """
        Usage: apipin run <workspace-folder> [options]

        Runs the requests of a collection (or one folder of it) in order, with captures and assertions,
        and exits with 0 when everything passed, 1 when something failed, 2 for usage errors.

        Options:
          -c, --collection <name>   Collection to run (name or folder name). Required when there are several.
          -f, --folder <path>       Only this folder, e.g. "Orders/Admin".
          -e, --env <name>          Environment to use.
          -v, --var <key=value>     Set a variable (wins over everything else). Repeatable.
                                    APIPIN_VAR_<key> environment variables work too, handy for CI secrets.
          -n, --iterations <n>      Run everything n times (default 1).
              --delay <ms>          Wait between requests.
              --bail                Stop at the first failure.
          -k, --insecure            Don't verify TLS certificates.
              --timeout <ms>        Timeout per request (default: each request's own).
          -r, --reporter <name>     console (default), junit or json. Repeatable.
          -o, --out <file>          File for the junit/json report (default: apipin-results.xml / .json).
              --quiet               Console: only failures and the summary.

        Secret variables are read from this user's ApiPin secret store when available; in CI, pass them with
        --var or APIPIN_VAR_<key>.

        Example:
          apipin run ./api-tests -c "Orders API" -e Staging -r junit -o results.xml -v token=$TOKEN
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, IHttpExecutor? executor = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? ExitUsage : ExitPassed;
        }
        if (args[0] is "--version")
        {
            output.WriteLine(typeof(CliApp).Assembly.GetName().Version);
            return ExitPassed;
        }
        if (args[0] != "run")
        {
            error.WriteLine($"Unknown command '{args[0]}'.");
            error.WriteLine(Usage);
            return ExitUsage;
        }

        Options options;
        try
        {
            options = Options.Parse(args[1..]);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine("Run 'apipin --help' for the options.");
            return ExitUsage;
        }

        if (!Directory.Exists(options.Workspace) || !File.Exists(Path.Combine(options.Workspace, WorkspaceStore.WorkspaceFileName)))
        {
            error.WriteLine($"'{options.Workspace}' is not an ApiPin workspace (no {WorkspaceStore.WorkspaceFileName}).");
            return ExitUsage;
        }

        ISecretStore secrets = OperatingSystem.IsWindows() ? new DpapiSecretStore(DpapiSecretStore.DefaultPath) : new InMemorySecretStore();
        var workspace = new WorkspaceStore(options.Workspace, secrets).Load();
        foreach (var problem in workspace.Problems) error.WriteLine($"warning: {problem.Path}: {problem.Message}");

        // What to run.
        var collections = workspace.OrderedCollections.ToList();
        RequestCollection? collection;
        if (options.Collection is { } wanted)
        {
            collection = collections.FirstOrDefault(c => c.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                         ?? collections.FirstOrDefault(c => Slug.From(c.Name, "") == Slug.From(wanted, "?"));
            if (collection is null)
            {
                error.WriteLine($"No collection '{wanted}'. Collections: {string.Join(", ", collections.Select(c => c.Name))}.");
                return ExitUsage;
            }
        }
        else if (collections.Count == 1)
        {
            collection = collections[0];
        }
        else
        {
            error.WriteLine(collections.Count == 0 ? "The workspace has no collections." : $"Pick one with --collection: {string.Join(", ", collections.Select(c => c.Name))}.");
            return ExitUsage;
        }

        RequestFolder target = collection;
        var above = new List<RequestFolder>();
        if (options.Folder is { } folderPath)
        {
            foreach (var part in folderPath.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var next = target.Folders.FirstOrDefault(f => f.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (next is null)
                {
                    error.WriteLine($"No folder '{part}' in '{target.Name}'.");
                    return ExitUsage;
                }
                above.Add(target);
                target = next;
            }
        }

        EnvironmentDefinition? environment = null;
        if (options.Environment is { } envName)
        {
            environment = workspace.Environments.FirstOrDefault(e => e.Name.Equals(envName, StringComparison.OrdinalIgnoreCase));
            if (environment is null)
            {
                error.WriteLine($"No environment '{envName}'. Environments: {string.Join(", ", workspace.Environments.Select(e => e.Name))}.");
                return ExitUsage;
            }
            foreach (var missing in environment.MissingSecrets)
                error.WriteLine($"warning: secret '{missing.Key}' of '{environment.Name}' has no value here; pass it with --var {missing.Key}=…");
        }

        var items = CollectionRunner.Plan(target, above);
        if (items.Count == 0)
        {
            error.WriteLine($"'{target.Name}' has no requests.");
            return ExitUsage;
        }
        foreach (var item in items)
        {
            if (options.Insecure) item.Request.Settings.VerifyTls = false;
            if (options.TimeoutMs is { } t) item.Request.Settings.TimeoutMs = t;
        }

        var variables = new RunVariables { Environment = environment, Globals = workspace.Globals };
        var env = environmentVariables ?? Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
        foreach (var (key, value) in env)
            if (key.StartsWith("APIPIN_VAR_", StringComparison.OrdinalIgnoreCase) && key.Length > 11) variables.Runtime[key[11..]] = value;
        foreach (var (key, value) in options.Variables) variables.Runtime[key] = value;

        // Run.
        using var pool = new HandlerPool();
        executor ??= new HttpExecutor(pool, null, new TokenService(pool));
        output.WriteLine($"Running {collection.Name}{(target == collection ? "" : " / " + string.Join(" / ", above.Skip(1).Select(f => f.Name).Append(target.Name)))}" +
                         $"{(environment is null ? "" : $" with {environment.Name}")}: {items.Count} request{(items.Count == 1 ? "" : "s")}" +
                         $"{(options.Iterations > 1 ? $" × {options.Iterations}" : "")}");
        output.WriteLine();

        var progress = new SyncProgress<RunStep>(step =>
        {
            if (!options.Quiet || !step.Passed) output.Write(RunReporters.ConsoleStep(step, showPassedChecks: !options.Quiet, showIteration: options.Iterations > 1));
        });
        var result = await new CollectionRunner(executor).RunAsync(items, variables,
            new RunOptions { Iterations = options.Iterations, DelayMs = options.DelayMs, StopOnFailure = options.Bail }, progress, cancellationToken);

        output.WriteLine();
        output.WriteLine(Summary(result));

        foreach (var reporter in options.Reporters.Where(r => r != "console"))
        {
            var path = options.Output ?? (reporter == "junit" ? "apipin-results.xml" : "apipin-results.json");
            if (options.Reporters.Count(r => r != "console") > 1 && options.Output is not null)
                path = Path.ChangeExtension(options.Output, reporter == "junit" ? ".xml" : ".json");
            await File.WriteAllTextAsync(path, reporter == "junit" ? RunReporters.JUnit(result, collection.Name) : RunReporters.Json(result), cancellationToken);
            output.WriteLine($"Wrote {reporter} report to {Path.GetFullPath(path)}");
        }

        return result.AllPassed ? ExitPassed : ExitFailed;
    }

    private static string Summary(RunResult r) =>
        $"{(r.AllPassed ? "PASSED" : r.Cancelled ? "CANCELLED" : "FAILED")}: {r.Requests} requests ({r.FailedRequests} failed), " +
        $"{r.Assertions} assertions ({r.FailedAssertions} failed) in {r.Duration.TotalSeconds:0.00} s{(r.Stopped ? ", stopped at the first failure" : "")}";

    /// <summary>Reports on the calling thread, in order (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record Options
    {
        public string Workspace { get; init; } = "";
        public string? Collection { get; init; }
        public string? Folder { get; init; }
        public string? Environment { get; init; }
        public List<(string Key, string Value)> Variables { get; } = [];
        public int Iterations { get; init; } = 1;
        public int DelayMs { get; init; }
        public bool Bail { get; init; }
        public bool Insecure { get; init; }
        public bool Quiet { get; init; }
        public int? TimeoutMs { get; init; }
        public List<string> Reporters { get; } = [];
        public string? Output { get; init; }

        public static Options Parse(string[] args)
        {
            string? workspace = null, collection = null, folder = null, env = null, output = null;
            int iterations = 1, delay = 0;
            int? timeout = null;
            bool bail = false, insecure = false, quiet = false;
            var vars = new List<(string, string)>();
            var reporters = new List<string>();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{arg} needs a value.");
                int Number(int min) => int.TryParse(Value(), out var n) && n >= min ? n : throw new ArgumentException($"{arg} needs a number ≥ {min}.");

                switch (arg)
                {
                    case "-c" or "--collection": collection = Value(); break;
                    case "-f" or "--folder": folder = Value(); break;
                    case "-e" or "--env" or "--environment": env = Value(); break;
                    case "-v" or "--var":
                        var pair = Value();
                        var eq = pair.IndexOf('=');
                        if (eq <= 0) throw new ArgumentException($"--var needs key=value, got '{pair}'.");
                        vars.Add((pair[..eq], pair[(eq + 1)..]));
                        break;
                    case "-n" or "--iterations": iterations = Number(1); break;
                    case "--delay": delay = Number(0); break;
                    case "--timeout": timeout = Number(0); break;
                    case "--bail": bail = true; break;
                    case "-k" or "--insecure": insecure = true; break;
                    case "--quiet": quiet = true; break;
                    case "-r" or "--reporter":
                        var reporter = Value().ToLowerInvariant();
                        if (reporter is not ("console" or "junit" or "json")) throw new ArgumentException($"Unknown reporter '{reporter}': use console, junit or json.");
                        reporters.Add(reporter);
                        break;
                    case "-o" or "--out": output = Value(); break;
                    default:
                        if (arg.StartsWith('-')) throw new ArgumentException($"Unknown option '{arg}'.");
                        if (workspace is not null) throw new ArgumentException($"Unexpected argument '{arg}'.");
                        workspace = arg;
                        break;
                }
            }
            if (workspace is null) throw new ArgumentException("Give the workspace folder: apipin run <folder>.");
            if (!reporters.Contains("console")) reporters.Insert(0, "console"); // always show progress

            var options = new Options
            {
                Workspace = Path.GetFullPath(workspace), Collection = collection, Folder = folder, Environment = env, Output = output,
                Iterations = iterations, DelayMs = delay, TimeoutMs = timeout, Bail = bail, Insecure = insecure, Quiet = quiet,
            };
            options.Variables.AddRange(vars);
            options.Reporters.AddRange(reporters.Distinct());
            return options;
        }
    }
}
