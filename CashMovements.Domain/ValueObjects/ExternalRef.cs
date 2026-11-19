using CashMovements.Domain.Exceptions;

namespace CashMovements.Domain.ValueObjects;

/// <summary>
/// The caller's reference for a movement. Unique across the whole system and used as the idempotency key.
/// Trimmed, non-blank, at most <see cref="MaxLength"/> characters.
/// </summary>
public sealed record ExternalRef
{
    public const int MaxLength = 64;

    private ExternalRef(string value) => Value = value;

    public string Value { get; }

    public static ExternalRef From(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidValueException("External reference is required.");

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
            throw new InvalidValueException($"External reference must be at most {MaxLength} characters.");

        return new ExternalRef(trimmed);
    }

    public override string ToString() => Value;
}
