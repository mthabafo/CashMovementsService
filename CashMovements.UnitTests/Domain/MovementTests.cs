using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;
using System.Globalization;

namespace CashMovements.UnitTests.Domain;

public class MovementTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider Clock = new FixedClock(Now);

    private static Movement Create(
        decimal amount = 100m,
        string currency = "ZAR",
        string accountId = "ACC1",
        string externalRef = "REF-1",
        DateTimeOffset? occurredAt = null,
        string? narration = "Deposit")
        => Movement.Create(accountId, externalRef, amount, currency, occurredAt ?? Now.AddDays(-1), narration, Clock);

    [Fact]
    public void Create_keeps_amount_and_currency_as_separate_fields()
    {
        var movement = Create(amount: -250.75m, currency: "zar");

        Assert.Equal(-250.75m, movement.Amount);
        Assert.Equal("ZAR", movement.Currency.Code);
    }

    [Fact]
    public void Create_leaves_id_unassigned()
    {
        var movement = Create();

        Assert.Equal(0, movement.Id);
    }

    [Fact]
    public void Create_normalises_occurred_at_to_utc()
    {
        var local = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(2));

        var movement = Create(occurredAt: local);

        Assert.Equal(TimeSpan.Zero, movement.OccurredAt.Offset);
        Assert.Equal(local, movement.OccurredAt);
    }

    [Theory]
    [InlineData(100, true, false)]
    [InlineData(-100, false, true)]
    public void Sign_determines_credit_or_debit(decimal amount, bool isCredit, bool isDebit)
    {
        var movement = Create(amount: amount);

        Assert.Equal(isCredit, movement.IsCredit);
        Assert.Equal(isDebit, movement.IsDebit);
    }

    [Fact]
    public void Zero_amount_is_rejected()
        => Assert.Throws<InvalidMovementException>(() => Create(amount: 0m));

    [Theory]
    [InlineData("1.005")]
    [InlineData("-0.001")]
    public void More_than_two_decimal_places_is_rejected(string amount)
        => Assert.Throws<InvalidMovementException>(
            () => Create(amount: decimal.Parse(amount, CultureInfo.InvariantCulture)));

    [Fact]
    public void Trailing_zero_decimal_places_are_allowed()
        => Assert.Equal(1.5m, Create(amount: 1.500m).Amount);

    [Fact]
    public void Occurred_at_within_clock_skew_is_allowed()
        => Create(occurredAt: Now + Movement.MaxClockSkew);

    [Fact]
    public void Occurred_at_beyond_clock_skew_is_rejected()
        => Assert.Throws<InvalidMovementException>(
            () => Create(occurredAt: Now + Movement.MaxClockSkew + TimeSpan.FromSeconds(1)));

    [Fact]
    public void Invalid_value_objects_are_rejected()
    {
        Assert.Throws<InvalidValueException>(() => Create(accountId: " "));
        Assert.Throws<InvalidValueException>(() => Create(externalRef: ""));
        Assert.Throws<InvalidValueException>(() => Create(currency: "RAND"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_narration_is_stored_as_null(string? narration)
        => Assert.Null(Create(narration: narration).Narration);

    [Fact]
    public void Narration_is_trimmed()
        => Assert.Equal("Salary", Create(narration: "  Salary ").Narration);

    [Fact]
    public void Narration_has_a_maximum_length()
    {
        Create(narration: new string('n', Movement.MaxNarrationLength));
        Assert.Throws<InvalidMovementException>(
            () => Create(narration: new string('n', Movement.MaxNarrationLength + 1)));
    }

    [Fact]
    public void Rehydrate_skips_the_future_date_rule_but_requires_an_id()
    {
        var future = Now.AddYears(1);

        var movement = Movement.Rehydrate(42, "ACC1", "REF-1", 10m, "ZAR", future, null);

        Assert.Equal(42, movement.Id);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Movement.Rehydrate(0, "ACC1", "REF-1", 10m, "ZAR", Now, null));
    }

    [Fact]
    public void Same_payload_is_detected_regardless_of_id_and_formatting()
    {
        var occurred = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(2));
        var incoming = Create(amount: 1.5m, currency: "zar", accountId: " ACC1 ", occurredAt: occurred);
        var stored = Movement.Rehydrate(7, "ACC1", "REF-1", 1.50m, "ZAR", occurred.ToUniversalTime(), "Deposit");

        Assert.True(incoming.HasSamePayloadAs(stored));
    }

    [Fact]
    public void Different_amount_currency_account_date_or_narration_is_a_different_payload()
    {
        var original = Create();

        Assert.False(original.HasSamePayloadAs(Create(amount: 101m)));
        Assert.False(original.HasSamePayloadAs(Create(currency: "USD")));
        Assert.False(original.HasSamePayloadAs(Create(accountId: "ACC2")));
        Assert.False(original.HasSamePayloadAs(Create(occurredAt: Now.AddDays(-2))));
        Assert.False(original.HasSamePayloadAs(Create(narration: "Other")));
        Assert.False(original.HasSamePayloadAs(Create(externalRef: "REF-2")));
    }
}
