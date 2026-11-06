using System.Xml.Linq;
using ApiPin.Cli;

namespace ApiPin.IntegrationTests;

/// <summary>The sample workspace's log in → create → get → delete flow, run by the CLI against the EchoServer.</summary>
public sealed class CliTests(EchoServerFixture server) : IClassFixture<EchoServerFixture>, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apipin-cli-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sample
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !Directory.Exists(Path.Combine(dir, "samples", "echo-api-tests"))) dir = Path.GetDirectoryName(dir);
            return Path.Combine(dir ?? throw new DirectoryNotFoundException("samples/echo-api-tests not found"), "samples", "echo-api-tests");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CliApp.RunAsync(args, output, error, environmentVariables: new Dictionary<string, string>(), cancellationToken: Ct);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Sample_flow_runs_green_and_writes_a_junit_report()
    {
        Directory.CreateDirectory(_dir);
        var report = Path.Combine(_dir, "results.xml");

        var (exit, output, error) = await Run("run", Sample, "-e", "Local", "--var", $"baseUrl={server.BaseUrl}", "-r", "junit", "-o", report);

        Assert.True(exit == CliApp.ExitPassed, output + error);
        Assert.Contains("PASSED: 5 requests (0 failed)", output);
        Assert.Contains("{{token}} captured", output);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", File.ReadAllText(report));
        var suite = XDocument.Load(report).Root!.Element("testsuite")!;
        Assert.Equal("5", suite.Attribute("tests")!.Value);
        Assert.Equal("0", suite.Attribute("failures")!.Value);
        Assert.Equal(["Log in", "Create item", "Get item", "Delete item", "Item is gone"], suite.Elements("testcase").Select(t => t.Attribute("name")!.Value));
    }

    [Fact]
    public async Task Failures_exit_with_1_and_bail_stops_early()
    {
        // Nothing listens on port 1: the first request fails and --bail stops there.
        var (exit, output, _) = await Run("run", Sample, "-v", "baseUrl=http://127.0.0.1:1", "--bail", "--quiet", "--timeout", "2000");

        Assert.Equal(CliApp.ExitFailed, exit);
        Assert.Contains("stopped at the first failure", output);
        Assert.Contains("1 requests (1 failed)", output);
        Assert.Contains("✗ Items API / Log in", output);
    }

    [Fact]
    public async Task Usage_errors_exit_with_2()
    {
        Assert.Equal(CliApp.ExitUsage, (await Run()).Exit);
        Assert.Equal(CliApp.ExitUsage, (await Run("run")).Exit);
        Assert.Equal(CliApp.ExitUsage, (await Run("run", _dir)).Exit);
        var (exit, _, error) = await Run("run", Sample, "-e", "Nope");
        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("No environment 'Nope'", error);
        Assert.Equal(CliApp.ExitPassed, (await Run("--help")).Exit);
    }

    [Fact]
    public async Task Json_report_and_iterations()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "r.json");

        var (exit, _, _) = await Run("run", Sample, "-v", $"baseUrl={server.BaseUrl}", "-n", "2", "-r", "json", "-o", path);

        Assert.Equal(CliApp.ExitPassed, exit);
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal(10, json.GetProperty("requests").GetInt32());
        Assert.True(json.GetProperty("passed").GetBoolean());
    }
}
