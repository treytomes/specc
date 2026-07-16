using Microsoft.Extensions.Logging;

namespace IronLlm.Tests.Fixtures;

/// <summary>
/// Minimal in-process logger that captures log records for assertion.
/// Replaces Microsoft.Extensions.Logging.Testing which is not published
/// as a standalone NuGet package for .NET 10.
/// </summary>
public sealed class FakeLogger<T> : ILogger<T>, IDisposable
{
    private readonly List<FakeLogRecord> _records = [];

    public IReadOnlyList<FakeLogRecord> Records
    {
        get { lock (_records) return _records.ToArray(); }
    }

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_records)
            _records.Add(new FakeLogRecord(logLevel, formatter(state, exception), exception));
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Dispose() { }
}

public sealed record FakeLogRecord(LogLevel Level, string Message, Exception? Exception);
