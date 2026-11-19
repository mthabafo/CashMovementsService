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

    /// <summary>How far into the future <see cref="OccurredAt"/> may be, to tolerate clock drift between systems.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private Movement(
        long id,
        AccountId accountId,
        ExternalRef externalRef,
        decimal amount,
        Currency currency,
        DateTimeOffset occurredAt,
        DateTimeOffset recordedAt,
        string? narration)
    {
        Id = id;
        AccountId = accountId;
        ExternalRef = externalRef;
        Amount = amount;
        Currency = currency;
        OccurredAt = occurredAt;
        RecordedAt = recordedAt;
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

    /// <summary>When this system recorded the movement (UTC).</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>Optional free text, at most <see cref="MaxNarrationLength"/> characters. Blank is stored as null.</summary>
    public string? Narration { get; private set; }

    public bool IsCredit => Amount > 0;

    public bool IsDebit => Amount < 0;

    /// <summary>Creates a new, not-yet-persisted movement and enforces every rule.</summary>
    /// <param name="clock">Supplies "now" for <see cref="RecordedAt"/> and the future-date check.</param>
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

        if (amount == 0)
            throw new InvalidMovementException("Amount must not be zero.");
        if (decimal.Round(amount, MaxDecimalPlaces) != amount)
            throw new InvalidMovementException($"Amount must have at most {MaxDecimalPlaces} decimal places.");
        if (occurredUtc > now + MaxClockSkew)
            throw new InvalidMovementException("Occurred-at date must not be in the future.");

        return new Movement(
            id: 0,
            AccountId.From(accountId),
            ExternalRef.From(externalRef),
            amount,
            Currency.From(currency),
            occurredUtc,
            recordedAt: now,
            NormaliseNarration(narration));
    }

    /// <summary>
    /// Rebuilds a movement that has already been persisted (used by the repository).
    /// Value objects are still validated, but the future-date check is skipped: it only applies when recording.
    /// </summary>
    public static Movement Rehydrate(
        long id,
        string accountId,
        string externalRef,
        decimal amount,
        string currency,
        DateTimeOffset occurredAt,
        DateTimeOffset recordedAt,
        string? narration)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), id, "A persisted movement must have a positive Id.");

        return new Movement(
            id,
            AccountId.From(accountId),
            ExternalRef.From(externalRef),
            amount,
            Currency.From(currency),
            occurredAt.ToUniversalTime(),
            recordedAt.ToUniversalTime(),
            NormaliseNarration(narration));
    }

    /// <summary>
    /// True when <paramref name="other"/> carries the same business payload. Used for idempotency: the same
    /// <see cref="ExternalRef"/> with the same payload is a replay; with a different payload it is a conflict.
    /// <see cref="Id"/> and <see cref="RecordedAt"/> are ignored.
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
