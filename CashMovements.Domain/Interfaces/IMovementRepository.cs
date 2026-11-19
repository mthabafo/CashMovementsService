using CashMovements.Domain.Entities;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.Domain.Interfaces;

/// <summary>
/// Data access for movements. Deliberately thin: it stores and fetches movements and sums amounts.
/// Balances and statements are assembled by the domain (<see cref="ReadModels.Statement.Build"/>)
/// so the rules stay testable without a database.
/// </summary>
public interface IMovementRepository
{
    /// <summary>
    /// Finds the movement recorded on <paramref name="accountId"/> under <paramref name="externalRef"/>,
    /// or null if none exists. External references are unique per account.
    /// </summary>
    Task<Movement?> GetByExternalReferenceAsync(
        AccountId accountId,
        ExternalRef externalRef,
        CancellationToken cancellationToken);

    /// <summary>Inserts a new movement and returns it with its database-assigned <see cref="Movement.Id"/>.</summary>
    Task<Movement> AddAsync(Movement movement, CancellationToken cancellationToken);

    /// <summary>
    /// Sums the signed amounts for the account and currency. With <paramref name="before"/> set, only movements
    /// that occurred strictly before it are included (a statement's opening balance); otherwise all movements
    /// (the current balance). Returns 0 when there are none.
    /// </summary>
    Task<decimal> SumAsync(
        AccountId accountId,
        Currency currency,
        DateTimeOffset? before,
        CancellationToken cancellationToken);

    /// <summary>Lists movements for the account and currency that occurred in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    Task<IReadOnlyList<Movement>> ListAsync(
        AccountId accountId,
        Currency currency,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}
