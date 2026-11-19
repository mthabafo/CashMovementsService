using CashMovements.Domain.ReadModels;

namespace CashMovements.Application;

/// <summary>
/// A statement delivered as a stream: the <see cref="Header"/> (with the opening balance) is ready immediately;
/// <see cref="Lines"/> are read lazily from the database as the caller enumerates them, each carrying its running balance.
/// The closing balance is the last line's running balance, or the opening balance when there are no lines.
/// </summary>
/// <remarks><see cref="Lines"/> can be enumerated only once.</remarks>
public sealed record StatementStream(StatementHeader Header, IAsyncEnumerable<StatementLine> Lines);
