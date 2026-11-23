using System.Globalization;
using CashMovements.Domain.Exceptions;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.Domain.Entities;

/// <summary>
/// One cash movement on an account. Immutable once recorded: balances and statements are derived
/// from movements, never stored.
/// <para>
/// <see cref="Amount"/> is signed — positive is a credit (money in), negative is a debit (money out).
/// <see cref="Amount"/> and <see cref="Currency"/> are separate fields and are persisted as separate columns.
/// </para>
/// </summary>
public sealed class Movement
{
    public const int MaxDecimalPlaces = 2;
    public const int MaxNarrationLength = 140;

    /// <summary>The largest amount, either way, that fits the database column (<c>DECIMAL(19,2)</c>).</summary>
    public const decimal MaxAbsoluteAmount = 99_999_999_999_999_999.99m;

    /// <summary>How far into the future <see cref="OccurredAt"/> may be, to tolerate clock drift between systems.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private Movement(
        long id,
        AccountId accountId,
        ExternalRef externalRef,
        decimal amount,
        Currency currency,
        DateTimeOffset occurredAt,
        string? narration)
    {
        Id = id;
        AccountId = accountId;
        ExternalRef = externalRef;
        Amount = amount;
        Currency = currency;
        OccurredAt = occurredAt;
        Narration = narration;
    }

    /// <summary>Database identity. 0 until the movement has been persisted.</summary>
    public long Id { get; private set; }

    public AccountId AccountId { get; private set; }

    public ExternalRef ExternalRef { get; private set; }

    /// <summary>Signed amount: + credit, − debit. Never zero, at most <see cref="MaxDecimalPlaces"/> decimal places.</summary>
    public decimal Amount { get; private set; }

    public Currency Currency { get; private set; }

    /// <summary>When the movement happened (UTC). Drives statement ordering and periods.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Optional free text, at most <see cref="MaxNarrationLength"/> characters. Blank is stored as null.</summary>
    public string? Narration { get; private set; }

    public bool IsCredit => Amount > 0;

    public bool IsDebit => Amount < 0;

    /// <summary>Creates a new, not-yet-persisted movement and enforces every rule.</summary>
    /// <param name="clock">Supplies "now" for the future-date check.</param>
    public static Movement Create(
        string accountId,
        string externalRef,
        decimal amount,
        string currency,
        DateTimeOffset occurredAt,
        string? narration,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();
        var occurredUtc = occurredAt.ToUniversalTime();

        ValidateAmount(amount);
        if (occurredUtc > now + MaxClockSkew)
            throw new InvalidMovementException("Occurred-at date must not be in the future.");

        return new Movement(
            id: 0,
            AccountId.From(accountId),
            ExternalRef.From(externalRef),
            amount,
            Currency.From(currency),
            occurredUtc,
            NormaliseNarration(narration));
    }

    /// <summary>
    /// Rebuilds a movement that has already been persisted (used by the repository).
    /// Every invariant of a movement still holds (value objects, amount, narration), so an invalid movement can't exist
    /// however it is created. Only the future-date check is skipped: it is a rule about recording, and an old row
    /// compared against a later clock must never become unreadable.
    /// </summary>
    public static Movement Rehydrate(
        long id,
        string accountId,
        string externalRef,
        decimal amount,
        string currency,
        DateTimeOffset occurredAt,
        string? narration)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), id, "A persisted movement must have a positive Id.");

        ValidateAmount(amount);

        return new Movement(
            id,
            AccountId.From(accountId),
            ExternalRef.From(externalRef),
            amount,
            Currency.From(currency),
            occurredAt.ToUniversalTime(),
            NormaliseNarration(narration));
    }

    /// <summary>
    /// True when <paramref name="other"/> carries the same business payload. Used for idempotency: the same
    /// <see cref="ExternalRef"/> with the same payload is a replay; with a different payload it is a conflict.
    /// <see cref="Id"/> is ignored.
    /// </summary>
    public bool HasSamePayloadAs(Movement other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return ExternalRef == other.ExternalRef
            && AccountId == other.AccountId
            && Amount == other.Amount
            && Currency == other.Currency
            && OccurredAt == other.OccurredAt
            && Narration == other.Narration;
    }

    /// <summary>The amount invariants, shared by <see cref="Create"/> and <see cref="Rehydrate"/>.</summary>
    private static void ValidateAmount(decimal amount)
    {
        if (amount == 0)
            throw new InvalidMovementException("Amount must not be zero.");

        if (decimal.Round(amount, MaxDecimalPlaces) != amount)
            throw new InvalidMovementException($"Amount must have at most {MaxDecimalPlaces} decimal places.");

        if (Math.Abs(amount) > MaxAbsoluteAmount)
            throw new InvalidMovementException(
                $"Amount must be between -{MaxAbsoluteAmount.ToString(CultureInfo.InvariantCulture)} and {MaxAbsoluteAmount.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static string? NormaliseNarration(string? narration)
    {
        if (string.IsNullOrWhiteSpace(narration))
            return null;

        var trimmed = narration.Trim();
        if (trimmed.Length > MaxNarrationLength)
            throw new InvalidMovementException($"Narration must be at most {MaxNarrationLength} characters.");

        return trimmed;
    }
}
