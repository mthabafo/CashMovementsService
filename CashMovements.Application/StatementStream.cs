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

    public StatementStream(StatementHeader header, IAsyncEnumerable<StatementLine> lines, IAsyncDisposable source)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Lines = lines ?? throw new ArgumentNullException(nameof(lines));
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public StatementHeader Header { get; }

    public IAsyncEnumerable<StatementLine> Lines { get; }

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
