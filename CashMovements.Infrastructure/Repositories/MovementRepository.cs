using CashMovements.Domain.Entities;
using CashMovements.Domain.Interfaces;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.Infrastructure.Repositories;

// Stub: implemented once the database schema (001_schema.sql) is written.
public class MovementRepository : IMovementRepository
{
    public Task<Movement?> FindByExternalRefAsync(ExternalRef externalRef, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task<Movement?> TryAddAsync(Movement movement, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task<decimal> SumAsync(AccountId accountId, Currency currency, DateTimeOffset? before, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<Movement>> ListAsync(AccountId accountId, Currency currency, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        => throw new NotImplementedException();
}
