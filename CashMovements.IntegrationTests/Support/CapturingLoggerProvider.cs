using Microsoft.Extensions.Logging;

namespace CashMovements.IntegrationTests.Support;

/// <summary>
/// Collects every log entry the in-memory host writes, with its structured fields and the scopes it was written in
/// (via <see cref="ISupportExternalScope"/>, the same scopes the console formatters print).
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly List<CapturedLog> _entries = [];
    private IExternalScopeProvider? _scopes;

    public IReadOnlyList<CapturedLog> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private void Add(CapturedLog entry)
    {
        lock (_entries)
            _entries.Add(entry);
    }

    private static Dictionary<string, object?> Fields(object? state)
        => state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.Where(p => p.Key != "{OriginalFormat}").GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value)
            : [];

    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, object?>();
            provider._scopes?.ForEachScope((value, all) =>
            {
                foreach (var pair in Fields(value))
                    all[pair.Key] = pair.Value;
            }, scope);

            provider.Add(new CapturedLog(category, logLevel, eventId, formatter(state, exception), Fields(state), scope, exception));
        }
    }
}

internal sealed record CapturedLog(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyDictionary<string, object?> Fields,
    IReadOnlyDictionary<string, object?> Scope,
    Exception? Exception)
{
    /// <summary>Everything that would reach a log store: the message, every field and scope value, and the exception.</summary>
    public string AllText
        => string.Join(" | ", new[] { Message }
            .Concat(Fields.Values.Select(v => v?.ToString()))
            .Concat(Scope.Values.Select(v => v?.ToString()))
            .Append(Exception?.ToString()));
}
