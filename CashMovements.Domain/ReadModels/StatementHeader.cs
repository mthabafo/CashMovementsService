using CashMovements.Domain.Exceptions;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.Domain.ReadModels;

/// <summary>
/// What a statement covers: one account in one currency over [<see cref="From"/>, <see cref="To"/>),
/// plus the balance carried in. Known before the first line is read, so it can be sent first when streaming.
/// </summary>
public sealed record StatementHeader
{
    private StatementHeader(AccountId accountId, Currency currency, DateTimeOffset from, DateTimeOffset to, decimal openingBalance)
    {
        AccountId = accountId;
        Currency = currency;
        From = from;
        To = to;
        OpeningBalance = openingBalance;
    }

    public AccountId AccountId { get; }

    public Currency Currency { get; }

    /// <summary>Inclusive start of the period (UTC).</summary>
    public DateTimeOffset From { get; }

    /// <summary>Exclusive end of the period (UTC).</summary>
    public DateTimeOffset To { get; }

    /// <summary>Sum of all movements that occurred before <see cref="From"/>.</summary>
    public decimal OpeningBalance { get; }

    /// <exception cref="InvalidValueException">The period is empty or reversed.</exception>
    public static StatementHeader Create(
        AccountId accountId,
        Currency currency,
        DateTimeOffset from,
        DateTimeOffset to,
        decimal openingBalance)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        ArgumentNullException.ThrowIfNull(currency);
        EnsureValidPeriod(from, to);

        return new StatementHeader(accountId, currency, from.ToUniversalTime(), to.ToUniversalTime(), openingBalance);
    }

    /// <summary>Checks a period without building a header, so callers can fail before querying.</summary>
    /// <exception cref="InvalidValueException">The period is empty or reversed.</exception>
    public static void EnsureValidPeriod(DateTimeOffset from, DateTimeOffset to)
    {
        if (from >= to)
            throw new InvalidValueException("Statement period start must be before its end.");
    }
}
