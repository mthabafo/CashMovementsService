using CashMovements.Domain.Entities;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.Domain.Interfaces;

/// <summary>
/// Data access for movements. Deliberately thin: it stores and fetches movements and sums amounts.
/// Balances and statements are assembled above it, so the rules stay testable without a database.
/// <para>
/// Naming: <c>Find…</c> may return null, <c>Add…</c> inserts, <c>Sum…</c> aggregates, <c>Stream…</c> yields rows lazily.
/// </para>
/// </summary>
public interface IMovementRepository
{
    /// <summary>
    /// Finds the movement recorded on <paramref name="accountId"/> under <paramref name="externalRef"/>,
    /// or null if none exists. External references are unique per account.
    /// </summary>
    Task<Movement?> FindByExternalRefAsync(
        AccountId accountId,
        ExternalRef externalRef,
        CancellationToken cancellationToken);

    /// <summary>Inserts a new movement and returns it with its database-assigned <see cref="Movement.Id"/>.</summary>
    Task<Movement> AddAsync(Movement movement, CancellationToken cancellationToken);

    /// <summary>Sum of all signed amounts for the account and currency (the current balance). 0 when there are none.</summary>
    Task<decimal> SumAmountsAsync(
        AccountId accountId,
        Currency currency,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sum of signed amounts for movements that occurred strictly before <paramref name="before"/>
    /// (a statement's opening balance). 0 when there are none.
    /// </summary>
    Task<decimal> SumAmountsBeforeAsync(
        AccountId accountId,
        Currency currency,
        DateTimeOffset before,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streams movements for the account and currency that occurred in [<paramref name="from"/>, <paramref name="to"/>),
    /// ordered by <see cref="Movement.OccurredAt"/> then <see cref="Movement.Id"/>. Rows are yielded as they are read,
    /// so a large period is never held in memory.
    /// </summary>
    IAsyncEnumerable<Movement> StreamInPeriodAsync(
        AccountId accountId,
        Currency currency,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}
