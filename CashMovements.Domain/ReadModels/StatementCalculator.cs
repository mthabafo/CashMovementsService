using CashMovements.Domain.Entities;

namespace CashMovements.Domain.ReadModels;

/// <summary>
/// Turns a stream of movements into statement lines one at a time, carrying the running balance.
/// Holds no list, so a statement of any length uses constant memory.
/// <para>
/// A stream cannot be re-sorted, so movements must arrive ordered by <see cref="Movement.OccurredAt"/> then
/// <see cref="Movement.Id"/> (the repository's <c>ORDER BY</c>). Anything out of order, for another account or
/// currency, or outside the period is rejected rather than silently producing wrong balances.
/// </para>
/// </summary>
public sealed class StatementCalculator
{
    private Movement? _previous;

    public StatementCalculator(StatementHeader header)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        RunningBalance = header.OpeningBalance;
    }

    public StatementHeader Header { get; }

    /// <summary>Balance after the last line added; the opening balance before any line. Equals the closing balance at the end.</summary>
    public decimal RunningBalance { get; private set; }

    public int LineCount { get; private set; }

    /// <summary>Adds the next movement and returns its statement line.</summary>
    /// <exception cref="ArgumentException">The movement does not belong in this statement or is out of order.</exception>
    public StatementLine Add(Movement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);

        if (movement.AccountId != Header.AccountId || movement.Currency != Header.Currency)
            throw new ArgumentException(
                $"Movement {movement.Id} is for {movement.AccountId}/{movement.Currency}, not {Header.AccountId}/{Header.Currency}.",
                nameof(movement));

        if (movement.OccurredAt < Header.From || movement.OccurredAt >= Header.To)
            throw new ArgumentException(
                $"Movement {movement.Id} occurred at {movement.OccurredAt:O}, outside the statement period.",
                nameof(movement));

        if (_previous is not null && !IsAfter(movement, _previous))
            throw new ArgumentException(
                $"Movement {movement.Id} is out of order: movements must be ordered by occurred-at then Id.",
                nameof(movement));

        _previous = movement;
        RunningBalance += movement.Amount;
        LineCount++;

        return new StatementLine(movement, RunningBalance);
    }

    private static bool IsAfter(Movement current, Movement previous)
        => current.OccurredAt > previous.OccurredAt
        || (current.OccurredAt == previous.OccurredAt && current.Id > previous.Id);
}
