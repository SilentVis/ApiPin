using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ApiPin.Storage.Logging;

/// <summary>
/// Writes log lines to one file per day (<c>apipin-yyyyMMdd.log</c>) in a folder, and deletes files older
/// than <see cref="RetainedDays"/>. Writes happen on a background thread so logging never blocks the UI.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int RetainedDays = 14;
    private const long MaxFileBytes = 10 * 1024 * 1024;

    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;
    private readonly LogLevel _minLevel;
    private string? _currentPath;
    private bool _disposed;

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        Directory = directory;
        _minLevel = minLevel;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "ApiPin log writer" };
        _writer.Start();
        DeleteOldFiles();
    }

    public string Directory { get; }

    /// <summary>%APPDATA%\ApiPin\logs</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ApiPin", "logs");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line)
    {
        if (_disposed) return;
        // Drop the line rather than block the caller when the disk can't keep up.
        _queue.TryAdd(line);
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var path = PathFor(DateTime.Now);
                System.IO.Directory.CreateDirectory(Directory);
                using var writer = new StreamWriter(path, append: true, Encoding.UTF8);
                writer.Write(line);
                // Drain whatever else is waiting into the same open file.
                while (_queue.TryTake(out var next)) writer.Write(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
        }
    }

    private string PathFor(DateTime now)
    {
        var path = Path.Combine(Directory, $"apipin-{now:yyyyMMdd}.log");
        if (_currentPath is not null && _currentPath.StartsWith(path[..^4], StringComparison.OrdinalIgnoreCase) && Size(_currentPath) < MaxFileBytes)
            return _currentPath;

        // Roll over to apipin-yyyyMMdd_2.log etc. when a day's file gets too big.
        for (var i = 2; Size(path) >= MaxFileBytes; i++)
            path = Path.Combine(Directory, $"apipin-{now:yyyyMMdd}_{i}.log");
        return _currentPath = path;
    }

    private static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private void DeleteOldFiles()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            var cutoff = DateTime.Now.AddDays(-RetainedDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "apipin-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(' ').Append(Level(logLevel))
                .Append(' ').Append(category)
                .Append(": ").Append(formatter(state, exception))
                .AppendLine();
            if (exception is not null) sb.AppendLine(exception.ToString());
            provider.Enqueue(sb.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}
