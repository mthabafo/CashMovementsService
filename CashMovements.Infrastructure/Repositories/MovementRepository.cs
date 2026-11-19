using CashMovements.Domain.Entities;
using CashMovements.Domain.Interfaces;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.Infrastructure.Repositories;

// Stub: implemented once the database schema (001_schema.sql) is written.
// When implemented, SumAmountsBeforeAsync + StreamInPeriodAsync for one statement should read from the same
// snapshot (e.g. one SNAPSHOT-isolation transaction) so opening, running and closing balances agree.
public class MovementRepository : IMovementRepository
{
    public Task<Movement?> FindByExternalRefAsync(AccountId accountId, ExternalRef externalRef, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task<Movement> AddAsync(Movement movement, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task<decimal> SumAmountsAsync(AccountId accountId, Currency currency, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task<decimal> SumAmountsBeforeAsync(AccountId accountId, Currency currency, DateTimeOffset before, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public IAsyncEnumerable<Movement> StreamInPeriodAsync(AccountId accountId, Currency currency, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        => throw new NotImplementedException();
}
