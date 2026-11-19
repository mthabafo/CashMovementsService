using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;
using CashMovements.Domain.ReadModels;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.UnitTests.Domain;

public class StatementTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly AccountId Account = AccountId.From("ACC1");
    private static readonly Currency Zar = Currency.From("ZAR");

    private static Movement Stored(long id, decimal amount, DateTimeOffset occurredAt, string currency = "ZAR", string accountId = "ACC1")
        => Movement.Rehydrate(id, accountId, $"REF-{id}", amount, currency, occurredAt, null);

    private static StatementCalculator Calculator(decimal opening = 0m)
        => new(StatementHeader.Create(Account, Zar, From, To, opening));

    // ---- StatementHeader ----

    [Fact]
    public void Header_rejects_a_reversed_or_empty_period()
    {
        Assert.Throws<InvalidValueException>(() => StatementHeader.Create(Account, Zar, To, From, 0m));
        Assert.Throws<InvalidValueException>(() => StatementHeader.Create(Account, Zar, From, From, 0m));
    }

    [Fact]
    public void Header_normalises_period_bounds_to_utc()
    {
        var header = StatementHeader.Create(Account, Zar, From.ToOffset(TimeSpan.FromHours(2)), To, 0m);

        Assert.Equal(TimeSpan.Zero, header.From.Offset);
        Assert.Equal(From, header.From);
    }

    // ---- StatementCalculator ----

    [Fact]
    public void Running_balance_starts_from_the_opening_balance_and_can_go_negative()
    {
        var calculator = Calculator(opening: 50m);

        var lines = new[]
        {
            calculator.Add(Stored(1, 100m, From.AddDays(1))),
            calculator.Add(Stored(2, -30m, From.AddDays(2))),
            calculator.Add(Stored(3, -500m, From.AddDays(3))),
        };

        Assert.Equal(new[] { 150m, 120m, -380m }, lines.Select(l => l.RunningBalance));
        Assert.Equal(-380m, calculator.RunningBalance);
        Assert.Equal(3, calculator.LineCount);
    }

    [Fact]
    public void Without_lines_the_running_balance_is_the_opening_balance()
    {
        var calculator = Calculator(opening: 75.25m);

        Assert.Equal(75.25m, calculator.RunningBalance);
        Assert.Equal(0, calculator.LineCount);
    }

    [Fact]
    public void Movements_at_the_same_instant_are_accepted_in_id_order()
    {
        var calculator = Calculator();
        var sameTime = From.AddDays(5);

        calculator.Add(Stored(2, 1m, sameTime));
        calculator.Add(Stored(3, 1m, sameTime));

        Assert.Equal(2, calculator.LineCount);
    }

    [Fact]
    public void Out_of_order_movements_are_rejected()
    {
        var calculator = Calculator();
        calculator.Add(Stored(5, 1m, From.AddDays(5)));

        Assert.Throws<ArgumentException>(() => calculator.Add(Stored(6, 1m, From.AddDays(4))));  // earlier time
        Assert.Throws<ArgumentException>(() => calculator.Add(Stored(4, 1m, From.AddDays(5))));  // same time, lower Id
        Assert.Throws<ArgumentException>(() => calculator.Add(Stored(5, 1m, From.AddDays(5))));  // same movement twice
    }

    [Fact]
    public void A_rejected_movement_does_not_change_the_running_balance()
    {
        var calculator = Calculator(opening: 10m);

        Assert.Throws<ArgumentException>(() => calculator.Add(Stored(1, 99m, To)));

        Assert.Equal(10m, calculator.RunningBalance);
        Assert.Equal(0, calculator.LineCount);
    }

    [Fact]
    public void Period_is_inclusive_at_the_start_and_exclusive_at_the_end()
    {
        Calculator().Add(Stored(1, 10m, From));

        Assert.Throws<ArgumentException>(() => Calculator().Add(Stored(2, 10m, To)));
        Assert.Throws<ArgumentException>(() => Calculator().Add(Stored(3, 10m, From.AddTicks(-1))));
    }

    [Fact]
    public void Movements_for_another_account_or_currency_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Calculator().Add(Stored(1, 10m, From, currency: "USD")));
        Assert.Throws<ArgumentException>(() => Calculator().Add(Stored(2, 10m, From, accountId: "ACC2")));
    }
}
