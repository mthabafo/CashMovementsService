using System.Runtime.CompilerServices;
using CashMovements.Domain.Entities;
using CashMovements.Domain.Interfaces;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.UnitTests.Application;

/// <summary>Test double that behaves like the real repository: assigns Ids, filters, sums and orders in memory.</summary>
internal sealed class InMemoryMovementRepository : IMovementRepository
{
    private readonly List<Movement> _movements = [];

    public IReadOnlyList<Movement> Movements => _movements;

    public int AddCalls { get; private set; }

    public int StreamCalls { get; private set; }

    /// <summary>Seeds an already-persisted movement directly, bypassing the service.</summary>
    public void Seed(Movement movement) => _movements.Add(movement);

    public Task<Movement?> FindByExternalRefAsync(AccountId accountId, ExternalRef externalRef, CancellationToken cancellationToken)
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

    public Task<decimal> SumAmountsAsync(AccountId accountId, Currency currency, CancellationToken cancellationToken)
        => Task.FromResult(For(accountId, currency).Sum(m => m.Amount));

    public Task<decimal> SumAmountsBeforeAsync(AccountId accountId, Currency currency, DateTimeOffset before, CancellationToken cancellationToken)
        => Task.FromResult(For(accountId, currency).Where(m => m.OccurredAt < before).Sum(m => m.Amount));

    public async IAsyncEnumerable<Movement> StreamInPeriodAsync(
        AccountId accountId,
        Currency currency,
        DateTimeOffset from,
        DateTimeOffset to,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        StreamCalls++;

        // Same ordering contract as the real query: ORDER BY OccurredAt, Id.
        var rows = For(accountId, currency)
            .Where(m => m.OccurredAt >= from && m.OccurredAt < to)
            .OrderBy(m => m.OccurredAt)
            .ThenBy(m => m.Id)
            .ToList();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return row;
        }
    }

    private IEnumerable<Movement> For(AccountId accountId, Currency currency)
        => _movements.Where(m => m.AccountId == accountId && m.Currency == currency);
}
