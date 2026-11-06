using ApiPin.Storage.Logging;
using Microsoft.Extensions.Logging;

namespace ApiPin.Storage.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apipin-tests", Guid.NewGuid().ToString("N"), "logs");

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_dir)!;
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public void Writes_lines_with_level_category_and_exception_to_a_daily_file()
    {
        using (var provider = new FileLoggerProvider(_dir))
        {
            var log = provider.CreateLogger("Test.Category");
            log.LogInformation("Hello {Name}", "world");
            log.LogDebug("Below the minimum level");
            log.LogError(new InvalidOperationException("boom"), "Failed");
        }

        var file = Assert.Single(Directory.GetFiles(_dir));
        Assert.Equal($"apipin-{DateTime.Now:yyyyMMdd}.log", Path.GetFileName(file));
        var text = File.ReadAllText(file);
        Assert.Contains("INF Test.Category: Hello world", text);
        Assert.Contains("ERR Test.Category: Failed", text);
        Assert.Contains("System.InvalidOperationException: boom", text);
        Assert.DoesNotContain("Below the minimum level", text);
    }

    [Fact]
    public void Deletes_files_older_than_the_retention_period()
    {
        Directory.CreateDirectory(_dir);
        var old = Path.Combine(_dir, "apipin-20200101.log");
        var recent = Path.Combine(_dir, "apipin-recent.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-FileLoggerProvider.RetainedDays - 1));

        using (new FileLoggerProvider(_dir)) { }

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }
}
