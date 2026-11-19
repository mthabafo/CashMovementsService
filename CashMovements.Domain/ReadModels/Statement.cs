using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.Domain.ReadModels;

/// <summary>One statement row: a movement plus the account's running balance after it.</summary>
public sealed record StatementLine(Movement Movement, decimal RunningBalance);

/// <summary>
/// The movements on one account in one currency over the period [<see cref="From"/>, <see cref="To"/>),
/// with opening, running and closing balances. Derived, never stored.
/// </summary>
public sealed class Statement
{
    private Statement(
        AccountId accountId,
        Currency currency,
        DateTimeOffset from,
        DateTimeOffset to,
        decimal openingBalance,
        IReadOnlyList<StatementLine> lines)
    {
        AccountId = accountId;
        Currency = currency;
        From = from;
        To = to;
        OpeningBalance = openingBalance;
        Lines = lines;
        ClosingBalance = lines.Count == 0 ? openingBalance : lines[^1].RunningBalance;
    }

    public AccountId AccountId { get; }

    public Currency Currency { get; }

    /// <summary>Inclusive start of the period (UTC).</summary>
    public DateTimeOffset From { get; }

    /// <summary>Exclusive end of the period (UTC).</summary>
    public DateTimeOffset To { get; }

    /// <summary>Sum of all movements that occurred before <see cref="From"/>.</summary>
    public decimal OpeningBalance { get; }

    /// <summary>Movements in the period, ordered by <see cref="Movement.OccurredAt"/> then <see cref="Movement.Id"/>.</summary>
    public IReadOnlyList<StatementLine> Lines { get; }

    /// <summary>Opening balance plus every movement in the period.</summary>
    public decimal ClosingBalance { get; }

    /// <summary>
    /// Builds a statement from the opening balance and the period's movements, computing running balances.
    /// Pure: no I/O, so it can be unit-tested without a database.
    /// </summary>
    /// <exception cref="InvalidValueException">The period is empty or reversed.</exception>
    /// <exception cref="ArgumentException">A movement belongs to another account/currency or falls outside the period.</exception>
    public static Statement Build(
        AccountId accountId,
        Currency currency,
        DateTimeOffset from,
        DateTimeOffset to,
        decimal openingBalance,
        IEnumerable<Movement> movements)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(movements);

        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();
        if (fromUtc >= toUtc)
            throw new InvalidValueException("Statement period start must be before its end.");

        var running = openingBalance;
        var lines = new List<StatementLine>();

        foreach (var movement in movements.OrderBy(m => m.OccurredAt).ThenBy(m => m.Id))
        {
            if (movement.AccountId != accountId || movement.Currency != currency)
                throw new ArgumentException(
                    $"Movement {movement.Id} is for {movement.AccountId}/{movement.Currency}, not {accountId}/{currency}.",
                    nameof(movements));
            if (movement.OccurredAt < fromUtc || movement.OccurredAt >= toUtc)
                throw new ArgumentException(
                    $"Movement {movement.Id} occurred at {movement.OccurredAt:O}, outside the statement period.",
                    nameof(movements));

            running += movement.Amount;
            lines.Add(new StatementLine(movement, running));
        }

        return new Statement(accountId, currency, fromUtc, toUtc, openingBalance, lines);
    }
}
