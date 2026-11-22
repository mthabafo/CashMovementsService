using Microsoft.Extensions.Logging;

namespace CashMovements.UnitTests.Support;

/// <summary>An <see cref="ILogger{T}"/> that keeps every entry, with its structured fields and the scopes it was written in.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<IReadOnlyDictionary<string, object?>> _scopes = [];
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        var values = Fields(state);
        lock (_entries)
            _scopes.Add(values);
        return new Scope(() => { lock (_entries) _scopes.Remove(values); });
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            var scope = new Dictionary<string, object?>();
            foreach (var values in _scopes)
                foreach (var pair in values)
                    scope[pair.Key] = pair.Value;

            _entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), Fields(state), scope, exception));
        }
    }

    private static IReadOnlyDictionary<string, object?> Fields(object? state)
        => state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.Where(p => p.Key != "{OriginalFormat}").ToDictionary(p => p.Key, p => p.Value)
            : new Dictionary<string, object?>();

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

internal sealed class LogEntry(
    LogLevel level,
    EventId eventId,
    string message,
    IReadOnlyDictionary<string, object?> fields,
    IReadOnlyDictionary<string, object?> scope,
    Exception? exception)
{
    public LogLevel Level { get; } = level;
    public EventId EventId { get; } = eventId;
    public string Message { get; } = message;
    public IReadOnlyDictionary<string, object?> Fields { get; } = fields;
    public IReadOnlyDictionary<string, object?> Scope { get; } = scope;
    public Exception? Exception { get; } = exception;

    /// <summary>Everything that would reach a log store: the message, every field and scope value, and the exception.</summary>
    public string AllText
        => string.Join(" | ", new[] { Message }
            .Concat(Fields.Values.Select(v => v?.ToString()))
            .Concat(Scope.Values.Select(v => v?.ToString()))
            .Concat([Exception?.ToString()]));
}
