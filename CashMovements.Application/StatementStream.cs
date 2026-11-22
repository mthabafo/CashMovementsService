using CashMovements.Domain.ReadModels;

namespace CashMovements.Application;

/// <summary>
/// A statement delivered as a stream: the <see cref="Header"/> (with the opening balance) is ready immediately;
/// <see cref="Lines"/> are read lazily from the database as the caller enumerates them, each carrying its running balance.
/// The closing balance is the last line's running balance, or the opening balance when there are no lines.
/// </summary>
/// <remarks>
/// <see cref="Lines"/> can be enumerated only once. The header and lines come from one database snapshot that stays
/// open until this is disposed, so always use <c>await using</c>.
/// </remarks>
public sealed class StatementStream : IAsyncDisposable
{
    private readonly IAsyncDisposable _source;
    private readonly Func<CancellationToken, Task<int>>? _countLines;

    public StatementStream(
        StatementHeader header,
        IAsyncEnumerable<StatementLine> lines,
        IAsyncDisposable source,
        Func<CancellationToken, Task<int>>? countLines = null)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Lines = lines ?? throw new ArgumentNullException(nameof(lines));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _countLines = countLines;
    }

    public StatementHeader Header { get; }

    public IAsyncEnumerable<StatementLine> Lines { get; }

    /// <summary>
    /// How many lines <see cref="Lines"/> will yield, from the same database snapshot, so it always matches. Costs one
    /// extra query, so it's only run when asked for (an export's progress total). Call it before enumerating the lines.
    /// </summary>
    public Task<int> CountLinesAsync(CancellationToken cancellationToken)
        => _countLines is null
            ? throw new NotSupportedException("This statement can't count its lines.")
            : _countLines(cancellationToken);

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
