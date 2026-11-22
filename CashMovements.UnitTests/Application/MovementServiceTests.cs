using System.Runtime.CompilerServices;
using CashMovements.Application;
using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;
using CashMovements.Domain.Interfaces;
using CashMovements.Domain.ReadModels;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.UnitTests.Application;

public class MovementServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider Clock = new FixedClock(Now);

    private readonly InMemoryMovementRepository _repository = new();
    private readonly MovementService _service;

    public MovementServiceTests() => _service = new MovementService(_repository, Clock);

    private static Movement New(
        decimal amount,
        string externalRef = "REF-1",
        string accountId = "ACC1",
        string currency = "ZAR",
        DateTimeOffset? occurredAt = null,
        string? narration = null)
        => Movement.Create(accountId, externalRef, amount, currency, occurredAt ?? Now.AddDays(-1), narration, Clock);

    // ---- RecordMovementAsync ----

    [Fact]
    public async Task Records_a_new_movement_and_returns_it_with_an_id()
    {
        var recorded = await _service.RecordMovementAsync(New(100m), default);

        Assert.True(recorded.Id > 0);
        Assert.Equal(100m, recorded.Amount);
        Assert.Single(_repository.Movements);
    }

    [Fact]
    public async Task Retry_with_same_amount_and_currency_returns_the_existing_movement_without_inserting()
    {
        var first = await _service.RecordMovementAsync(New(100m), default);

        var retry = await _service.RecordMovementAsync(New(100.00m, currency: "zar"), default);

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(1, _repository.AddCalls);
    }

    [Fact]
    public async Task Same_ref_with_different_amount_is_rejected()
    {
        await _service.RecordMovementAsync(New(100m), default);

        var ex = await Assert.ThrowsAsync<DuplicateMovementException>(
            () => _service.RecordMovementAsync(New(200m), default));

        Assert.Equal("REF-1", ex.ExternalRef.Value);
        Assert.Single(_repository.Movements);
    }

    [Fact]
    public async Task Same_ref_with_different_currency_is_rejected()
    {
        await _service.RecordMovementAsync(New(100m), default);

        await Assert.ThrowsAsync<DuplicateMovementException>(
            () => _service.RecordMovementAsync(New(100m, currency: "USD"), default));
    }

    [Fact]
    public async Task Same_ref_on_a_different_account_is_a_new_movement()
    {
        await _service.RecordMovementAsync(New(100m, accountId: "ACC1"), default);

        await _service.RecordMovementAsync(New(100m, accountId: "ACC2"), default);

        Assert.Equal(2, _repository.Movements.Count);
    }

    [Fact]
    public async Task Retry_only_compares_amount_and_currency()
    {
        // Agreed behaviour for now: date and narration are not part of the idempotency check.
        var first = await _service.RecordMovementAsync(New(100m, narration: "Original"), default);

        var retry = await _service.RecordMovementAsync(
            New(100m, narration: "Changed", occurredAt: Now.AddDays(-3)), default);

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal("Original", retry.Narration);
    }

    // Two identical requests at the same moment: both look up the ref and find nothing, then the database's unique key
    // lets only one insert through. The one that loses must be treated as a retry, not reported as a duplicate.

    private static Movement Stored(long id, decimal amount, string currency = "ZAR")
        => Movement.Rehydrate(id, "ACC1", "REF-1", amount, currency, Now.AddDays(-1), null);

    [Fact]
    public async Task Losing_the_insert_race_to_an_identical_request_returns_the_winners_movement()
    {
        _repository.SimulateConcurrentInsert(Stored(id: 42, amount: 100m));

        var recorded = await _service.RecordMovementAsync(New(100m), default);

        Assert.Equal(42, recorded.Id);
        Assert.Equal(1, _repository.AddCalls);
        Assert.Single(_repository.Movements);
    }

    [Fact]
    public async Task Losing_the_insert_race_to_a_different_amount_is_a_duplicate()
    {
        _repository.SimulateConcurrentInsert(Stored(id: 42, amount: 999m));

        var ex = await Assert.ThrowsAsync<DuplicateMovementException>(
            () => _service.RecordMovementAsync(New(100m), default));

        Assert.Equal("REF-1", ex.ExternalRef.Value);
    }

    [Fact]
    public async Task Losing_the_insert_race_to_a_different_currency_is_a_duplicate()
    {
        _repository.SimulateConcurrentInsert(Stored(id: 42, amount: 100m, currency: "USD"));

        await Assert.ThrowsAsync<DuplicateMovementException>(
            () => _service.RecordMovementAsync(New(100m), default));
    }

    [Fact]
    public async Task A_duplicate_error_with_no_existing_row_is_not_swallowed()
    {
        _repository.SimulateConcurrentInsert(winner: null);

        await Assert.ThrowsAsync<DuplicateMovementException>(
            () => _service.RecordMovementAsync(New(100m), default));
    }

    // ---- GetBalanceAsync ----

    [Fact]
    public async Task Balance_of_an_account_without_movements_is_zero()
    {
        var balance = await _service.GetBalanceAsync("ACC1", "ZAR", default);

        Assert.Equal(0m, balance.Amount);
        Assert.Equal(Now, balance.AsAt);
    }

    [Fact]
    public async Task Balance_sums_signed_amounts_for_that_account_and_currency_only()
    {
        await _service.RecordMovementAsync(New(100m, "R1"), default);
        await _service.RecordMovementAsync(New(-250.50m, "R2"), default);
        await _service.RecordMovementAsync(New(999m, "R3", currency: "USD"), default);
        await _service.RecordMovementAsync(New(999m, "R4", accountId: "ACC2"), default);

        var balance = await _service.GetBalanceAsync(" ACC1 ", "zar", default);

        Assert.Equal(-150.50m, balance.Amount);
        Assert.Equal("ZAR", balance.Currency.Code);
    }

    [Fact]
    public async Task Balance_rejects_an_invalid_currency()
        => await Assert.ThrowsAsync<InvalidValueException>(() => _service.GetBalanceAsync("ACC1", "RAND", default));

    // ---- GetStatementAsync ----

    [Fact]
    public async Task Statement_streams_header_with_opening_balance_then_lines_with_running_balances()
    {
        var from = Now.AddDays(-10);
        var to = Now.AddDays(-2);
        // Recorded out of time order on purpose: the stream must still come back ordered by occurred-at.
        await _service.RecordMovementAsync(New(50m, "R3", occurredAt: from.AddDays(2)), default);
        await _service.RecordMovementAsync(New(1000m, "R1", occurredAt: from.AddDays(-1)), default); // before period
        await _service.RecordMovementAsync(New(-200m, "R2", occurredAt: from.AddDays(1)), default);
        await _service.RecordMovementAsync(New(7m, "R4", occurredAt: to), default);                   // after period (exclusive end)

        await using var statement = await _service.GetStatementAsync("ACC1", "ZAR", from, to, default);
        var lines = await ToListAsync(statement.Lines);

        Assert.Equal(1000m, statement.Header.OpeningBalance);
        Assert.Equal(new[] { "R2", "R3" }, lines.Select(l => l.Movement.ExternalRef.Value));
        Assert.Equal(new[] { 800m, 850m }, lines.Select(l => l.RunningBalance));
    }

    [Fact]
    public async Task Statement_lines_are_not_read_until_enumerated()
    {
        await using var statement = await _service.GetStatementAsync("ACC1", "ZAR", Now.AddDays(-1), Now, default);

        Assert.Equal(0, _repository.StreamCalls);

        await ToListAsync(statement.Lines);

        Assert.Equal(1, _repository.StreamCalls);
    }

    [Fact]
    public async Task Statement_with_no_movements_has_only_the_opening_balance()
    {
        await _service.RecordMovementAsync(New(40m, "R1", occurredAt: Now.AddDays(-20)), default);

        await using var statement = await _service.GetStatementAsync("ACC1", "ZAR", Now.AddDays(-10), Now, default);

        Assert.Equal(40m, statement.Header.OpeningBalance);
        Assert.Empty(await ToListAsync(statement.Lines));
    }

    [Fact]
    public async Task Statement_fails_mid_stream_if_the_repository_returns_rows_out_of_order()
    {
        // Guards the ordering contract: a buggy query must not silently produce wrong running balances.
        var service = new MovementService(new UnorderedRepository(), Clock);
        await using var statement = await service.GetStatementAsync("ACC1", "ZAR", Now.AddDays(-10), Now, default);

        await Assert.ThrowsAsync<ArgumentException>(() => ToListAsync(statement.Lines));
    }

    [Fact]
    public async Task Statement_rejects_a_reversed_period_before_querying()
    {
        await Assert.ThrowsAsync<InvalidValueException>(
            () => _service.GetStatementAsync("ACC1", "ZAR", Now, Now.AddDays(-1), default));

        Assert.Equal(0, _repository.OpenCalls);
    }

    [Fact]
    public async Task Statement_reads_opening_balance_and_lines_from_one_snapshot()
    {
        await _service.RecordMovementAsync(New(10m, "R1", occurredAt: Now.AddDays(-5)), default);

        await using var statement = await _service.GetStatementAsync("ACC1", "ZAR", Now.AddDays(-10), Now, default);

        // Inserted after the statement was opened, inside the period: must not appear.
        await _service.RecordMovementAsync(New(99m, "R2", occurredAt: Now.AddDays(-4)), default);
        var lines = await ToListAsync(statement.Lines);

        Assert.Equal(1, _repository.OpenCalls);
        Assert.Equal(new[] { "R1" }, lines.Select(l => l.Movement.ExternalRef.Value));
    }

    [Fact]
    public async Task Disposing_the_statement_releases_the_snapshot_even_if_lines_were_never_read()
    {
        var statement = await _service.GetStatementAsync("ACC1", "ZAR", Now.AddDays(-1), Now, default);
        Assert.Equal(1, _repository.OpenSources);

        await statement.DisposeAsync();
        await statement.DisposeAsync(); // idempotent

        Assert.Equal(0, _repository.OpenSources);
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    /// <summary>A repository whose stream ignores the ORDER BY contract.</summary>
    private sealed class UnorderedRepository : IMovementRepository
    {
        public Task<Movement?> FindByExternalRefAsync(AccountId a, ExternalRef r, CancellationToken ct) => Task.FromResult<Movement?>(null);
        public Task<Movement> AddAsync(Movement m, CancellationToken ct) => Task.FromResult(m);
        public Task<decimal> SumAmountsAsync(AccountId a, Currency c, CancellationToken ct) => Task.FromResult(0m);

        public Task<StatementSource> OpenStatementAsync(AccountId a, Currency c, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => Task.FromResult(new StatementSource(0m, Rows()));

        private static async IAsyncEnumerable<Movement> Rows()
        {
            await Task.Yield();
            yield return Movement.Rehydrate(2, "ACC1", "R2", 1m, "ZAR", Now.AddDays(-1), null);
            yield return Movement.Rehydrate(1, "ACC1", "R1", 1m, "ZAR", Now.AddDays(-2), null);
        }
    }
}
