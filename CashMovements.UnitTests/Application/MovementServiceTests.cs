using CashMovements.Application;
using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;

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
    public async Task Statement_has_opening_running_and_closing_balances()
    {
        var from = Now.AddDays(-10);
        var to = Now.AddDays(-2);
        await _service.RecordMovementAsync(New(1000m, "R1", occurredAt: from.AddDays(-1)), default); // before period
        await _service.RecordMovementAsync(New(-200m, "R2", occurredAt: from.AddDays(1)), default);
        await _service.RecordMovementAsync(New(50m, "R3", occurredAt: from.AddDays(2)), default);
        await _service.RecordMovementAsync(New(7m, "R4", occurredAt: to), default);                   // after period (exclusive end)

        var statement = await _service.GetStatementAsync("ACC1", "ZAR", from, to, default);

        Assert.Equal(1000m, statement.OpeningBalance);
        Assert.Equal(new[] { 800m, 850m }, statement.Lines.Select(l => l.RunningBalance));
        Assert.Equal(850m, statement.ClosingBalance);
    }

    [Fact]
    public async Task Statement_rejects_a_reversed_period()
        => await Assert.ThrowsAsync<InvalidValueException>(
            () => _service.GetStatementAsync("ACC1", "ZAR", Now, Now.AddDays(-1), default));
}
