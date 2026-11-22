using System.Runtime.CompilerServices;
using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;
using CashMovements.Domain.Interfaces;
using CashMovements.Domain.ReadModels;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.UnitTests.Application;

/// <summary>Test double that behaves like the real repository: assigns Ids, filters, sums and orders in memory.</summary>
internal sealed class InMemoryMovementRepository : IMovementRepository
{
    private readonly List<Movement> _movements = [];

    public IReadOnlyList<Movement> Movements => _movements;

    public int AddCalls { get; private set; }

    public int OpenCalls { get; private set; }

    /// <summary>Statement sources opened and not yet disposed.</summary>
    public int OpenSources { get; private set; }

    /// <summary>Times the statement movements were actually enumerated (proves laziness).</summary>
    public int StreamCalls { get; private set; }

    /// <summary>Seeds an already-persisted movement directly, bypassing the service.</summary>
    public void Seed(Movement movement) => _movements.Add(movement);

    private bool _simulateConcurrentInsert;
    private Movement? _concurrentWinner;

    /// <summary>
    /// Makes the next <see cref="AddAsync"/> behave as if another request inserted the same (account, ref) just before
    /// it: <paramref name="winner"/> (if any) is stored and the call fails with <see cref="DuplicateMovementException"/>,
    /// exactly like losing the race to SQL Server's unique key.
    /// </summary>
    public void SimulateConcurrentInsert(Movement? winner)
    {
        _simulateConcurrentInsert = true;
        _concurrentWinner = winner;
    }

    public Task<Movement?> FindByExternalRefAsync(AccountId accountId, ExternalRef externalRef, CancellationToken cancellationToken)
        => Task.FromResult(_movements.SingleOrDefault(m => m.AccountId == accountId && m.ExternalRef == externalRef));

    public Task<Movement> AddAsync(Movement movement, CancellationToken cancellationToken)
    {
        AddCalls++;

        if (_simulateConcurrentInsert)
        {
            _simulateConcurrentInsert = false;
            if (_concurrentWinner is not null)
                _movements.Add(_concurrentWinner);

            throw new DuplicateMovementException(movement.ExternalRef);
        }

        var stored = Movement.Rehydrate(
            _movements.Count + 1,
            movement.AccountId.Value,
            movement.ExternalRef.Value,
            movement.Amount,
            movement.Currency.Code,
            movement.OccurredAt,
            movement.Narration);
        _movements.Add(stored);
        return Task.FromResult(stored);
    }

    public Task<decimal> SumAmountsAsync(AccountId accountId, Currency currency, CancellationToken cancellationToken)
        => Task.FromResult(For(accountId, currency).Sum(m => m.Amount));

    public Task<StatementSource> OpenStatementAsync(
        AccountId accountId,
        Currency currency,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        OpenCalls++;
        OpenSources++;

        // Snapshot now, like the real repository: later inserts don't change this statement.
        var opening = For(accountId, currency).Where(m => m.OccurredAt < from).Sum(m => m.Amount);
        var rows = For(accountId, currency)
            .Where(m => m.OccurredAt >= from && m.OccurredAt < to)
            .OrderBy(m => m.OccurredAt)   // same ordering contract as the real query: ORDER BY OccurredAt, Id
            .ThenBy(m => m.Id)
            .ToList();

        return Task.FromResult(new StatementSource(opening, Stream(rows), new Release(() => OpenSources--)));
    }

    private async IAsyncEnumerable<Movement> Stream(
        List<Movement> rows,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamCalls++;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return row;
        }
    }

    private IEnumerable<Movement> For(AccountId accountId, Currency currency)
        => _movements.Where(m => m.AccountId == accountId && m.Currency == currency);

    private sealed class Release(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
