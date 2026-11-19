using CashMovements.Domain.Entities;
using CashMovements.Domain.ReadModels;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.Domain.Interfaces;

/// <summary>
/// Data access for movements. Deliberately thin: it stores and fetches movements and sums amounts.
/// Balances and statements are assembled above it, so the rules stay testable without a database.
/// <para>
/// Naming: <c>Find…</c> may return null, <c>Add…</c> inserts, <c>Sum…</c> aggregates,
/// <c>Open…</c> returns something that holds resources and must be disposed.
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
    /// <exception cref="Exceptions.DuplicateMovementException">
    /// The account already has a movement with this external reference (for example, two retries arriving together).
    /// </exception>
    Task<Movement> AddAsync(Movement movement, CancellationToken cancellationToken);

    /// <summary>Sum of all signed amounts for the account and currency (the current balance). 0 when there are none.</summary>
    Task<decimal> SumAmountsAsync(
        AccountId accountId,
        Currency currency,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a consistent snapshot for a statement: the opening balance (movements that occurred before
    /// <paramref name="from"/>) and the movements in [<paramref name="from"/>, <paramref name="to"/>), ordered by
    /// <see cref="Movement.OccurredAt"/> then <see cref="Movement.Id"/>. Both come from the same point in time, so a
    /// movement inserted meanwhile can never be counted twice or missed. Movements are yielded as they are read,
    /// so a large period is never held in memory. The caller must dispose the result.
    /// </summary>
    Task<StatementSource> OpenStatementAsync(
        AccountId accountId,
        Currency currency,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}
