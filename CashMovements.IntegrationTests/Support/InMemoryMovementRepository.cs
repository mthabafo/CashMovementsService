using System.Runtime.CompilerServices;
using CashMovements.Domain.Entities;
using CashMovements.Domain.Interfaces;
using CashMovements.Domain.ReadModels;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.IntegrationTests.Support;

/// <summary>
/// Stands in for SQL Server so the gRPC tests need no database. Honours the repository contract
/// (Ids assigned on insert, per-account refs, ORDER BY OccurredAt, Id) and can simulate a failure mid-stream.
/// </summary>
internal sealed class InMemoryMovementRepository : IMovementRepository
{
    private readonly List<Movement> _movements = [];
    private readonly object _gate = new();

    /// <summary>When set, the statement stream throws after yielding this many rows.</summary>
    public int? FailStreamAfterRows { get; set; }

    private int _openSources;

    /// <summary>Statement sources opened and not yet disposed. Must be back to 0 once a call has finished.</summary>
    public int OpenSources => Volatile.Read(ref _openSources);

    public Task<Movement?> FindByExternalRefAsync(AccountId accountId, ExternalRef externalRef, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(_movements.SingleOrDefault(m => m.AccountId == accountId && m.ExternalRef == externalRef));
    }

    public Task<Movement> AddAsync(Movement movement, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
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
    }

    public Task<decimal> SumAmountsAsync(AccountId accountId, Currency currency, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(For(accountId, currency).Sum(m => m.Amount));
    }

    public Task<StatementSource> OpenStatementAsync(
        AccountId accountId,
        Currency currency,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        decimal opening;
        List<Movement> rows;
        lock (_gate)
        {
            opening = For(accountId, currency).Where(m => m.OccurredAt < from).Sum(m => m.Amount);
            rows = For(accountId, currency)
                .Where(m => m.OccurredAt >= from && m.OccurredAt < to)
                .OrderBy(m => m.OccurredAt)
                .ThenBy(m => m.Id)
                .ToList();
        }

        Interlocked.Increment(ref _openSources);
        return Task.FromResult(new StatementSource(
            opening,
            Stream(rows),
            new Release(() => Interlocked.Decrement(ref _openSources)),
            _ => Task.FromResult(rows.Count))); // same snapshot as the rows, like the real repository
    }

    private async IAsyncEnumerable<Movement> Stream(
        List<Movement> rows,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var yielded = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();

            if (yielded == FailStreamAfterRows)
                throw new InvalidOperationException("Simulated database failure: connection string and secret details.");

            yielded++;
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
