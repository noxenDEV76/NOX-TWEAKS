using System.Collections.Concurrent;

namespace NoxTweak.Core.Logging;

public enum LogLevel { Info, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}";
}

public interface IAppLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    IReadOnlyList<LogEntry> Recent { get; }
}

/// <summary>Local-only log: a rolling in-memory buffer plus a daily text file. Nothing is uploaded.</summary>
public sealed class AppLog : IAppLog
{
    private readonly string? _dir;
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private readonly object _lock = new();

    public AppLog(string? directory)
    {
        _dir = directory;
        if (_dir is not null)
        {
            try { Directory.CreateDirectory(_dir); } catch { _dir = null; }
        }
    }

    public IReadOnlyList<LogEntry> Recent => _recent.ToArray();

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warning, message);
    public void Error(string message) => Write(LogLevel.Error, message);

    private void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        _recent.Enqueue(entry);
        while (_recent.Count > 500) _recent.TryDequeue(out _);
        if (_dir is null) return;
        try
        {
            lock (_lock)
                File.AppendAllText(Path.Combine(_dir, $"nox-{DateTime.Now:yyyyMMdd}.log"), entry + Environment.NewLine);
        }
        catch { /* logging must never crash the app */ }
    }
}
