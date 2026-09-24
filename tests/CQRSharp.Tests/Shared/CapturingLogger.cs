using Microsoft.Extensions.Logging;

namespace CQRSharp.Tests.Shared;

/// <summary>A captured log entry: level, event id, rendered message and the attached exception.</summary>
public sealed record CapturedLogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

/// <summary>
///     An in-memory <see cref="ILogger{T}" /> that captures every entry. Asserting on what a component logged through a
///     real logger is sturdier than mocking <c>ILogger.Log&lt;TState&gt;</c>, which every logging call funnels through.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<CapturedLogEntry> _entries = [];

    /// <summary>A snapshot of the entries logged so far, in order.</summary>
    public IReadOnlyList<CapturedLogEntry> Entries
    {
        get
        {
            lock (_entries) return _entries.ToArray();
        }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add(new CapturedLogEntry(logLevel, eventId, formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
