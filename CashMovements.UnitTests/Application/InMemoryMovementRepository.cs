using CashMovements.Domain.Entities;
using CashMovements.Domain.Interfaces;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.UnitTests.Application;

/// <summary>Test double that behaves like the real repository: assigns Ids, filters and sums in memory.</summary>
internal sealed class InMemoryMovementRepository : IMovementRepository
{
    private readonly List<Movement> _movements = [];

    public IReadOnlyList<Movement> Movements => _movements;

    public int AddCalls { get; private set; }

    public Task<Movement?> GetByExternalReferenceAsync(AccountId accountId, ExternalRef externalRef, CancellationToken cancellationToken)
        => Task.FromResult(_movements.SingleOrDefault(m => m.AccountId == accountId && m.ExternalRef == externalRef));

    public Task<Movement> AddAsync(Movement movement, CancellationToken cancellationToken)
    {
        AddCalls++;
        var stored = Movement.Rehydrate(
            _movements.Count + 1,
            movement.AccountId.Value,
            movement.ExternalRef.Value,
            movement.Amount,
            movement.Currency.Code,
            movement.OccurredAt,
            movement.RecordedAt,
            movement.Narration);
        _movements.Add(stored);
        return Task.FromResult(stored);
    }

    public Task<decimal> SumAsync(AccountId accountId, Currency currency, DateTimeOffset? before, CancellationToken cancellationToken)
        => Task.FromResult(For(accountId, currency)
            .Where(m => before is null || m.OccurredAt < before)
            .Sum(m => m.Amount));

    public Task<IReadOnlyList<Movement>> ListAsync(AccountId accountId, Currency currency, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Movement>>(For(accountId, currency)
            .Where(m => m.OccurredAt >= from && m.OccurredAt < to)
            .ToList());

    private IEnumerable<Movement> For(AccountId accountId, Currency currency)
        => _movements.Where(m => m.AccountId == accountId && m.Currency == currency);
}
