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
        => Movement.Rehydrate(id, accountId, $"REF-{id}", amount, currency, occurredAt, occurredAt, null);

    private static Statement Build(decimal opening, params Movement[] movements)
        => Statement.Build(Account, Zar, From, To, opening, movements);

    [Fact]
    public void Running_balance_starts_from_the_opening_balance_and_can_go_negative()
    {
        var statement = Build(
            50m,
            Stored(1, 100m, From.AddDays(1)),
            Stored(2, -30m, From.AddDays(2)),
            Stored(3, -500m, From.AddDays(3)));

        Assert.Equal(50m, statement.OpeningBalance);
        Assert.Equal(new[] { 150m, 120m, -380m }, statement.Lines.Select(l => l.RunningBalance));
        Assert.Equal(-380m, statement.ClosingBalance);
    }

    [Fact]
    public void Lines_are_ordered_by_occurred_at_then_id()
    {
        var sameTime = From.AddDays(5);

        var statement = Build(
            0m,
            Stored(3, 1m, sameTime),
            Stored(9, 1m, From.AddDays(1)),
            Stored(2, 1m, sameTime));

        Assert.Equal(new long[] { 9, 2, 3 }, statement.Lines.Select(l => l.Movement.Id));
    }

    [Fact]
    public void Empty_period_closes_at_the_opening_balance()
    {
        var statement = Build(75.25m);

        Assert.Empty(statement.Lines);
        Assert.Equal(75.25m, statement.ClosingBalance);
    }

    [Fact]
    public void Period_is_inclusive_at_the_start_and_exclusive_at_the_end()
    {
        var statement = Build(0m, Stored(1, 10m, From));
        Assert.Single(statement.Lines);

        Assert.Throws<ArgumentException>(() => Build(0m, Stored(2, 10m, To)));
        Assert.Throws<ArgumentException>(() => Build(0m, Stored(3, 10m, From.AddTicks(-1))));
    }

    [Fact]
    public void Movements_for_another_account_or_currency_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Build(0m, Stored(1, 10m, From, currency: "USD")));
        Assert.Throws<ArgumentException>(() => Build(0m, Stored(2, 10m, From, accountId: "ACC2")));
    }

    [Fact]
    public void Reversed_or_empty_period_is_rejected()
    {
        Assert.Throws<InvalidValueException>(() => Statement.Build(Account, Zar, To, From, 0m, []));
        Assert.Throws<InvalidValueException>(() => Statement.Build(Account, Zar, From, From, 0m, []));
    }

    [Fact]
    public void Period_bounds_are_normalised_to_utc()
    {
        var localFrom = From.ToOffset(TimeSpan.FromHours(2));

        var statement = Statement.Build(Account, Zar, localFrom, To, 0m, []);

        Assert.Equal(TimeSpan.Zero, statement.From.Offset);
        Assert.Equal(From, statement.From);
    }
}
