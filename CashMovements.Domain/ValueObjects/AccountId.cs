using CashMovements.Domain.Exceptions;

namespace CashMovements.Domain.ValueObjects;

/// <summary>Identifies the account a movement belongs to. Trimmed, non-blank, at most <see cref="MaxLength"/> characters.</summary>
public sealed record AccountId
{
    public const int MaxLength = 34;

    private AccountId(string value) => Value = value;

    public string Value { get; }

    public static AccountId From(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidValueException("Account ID is required.");

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
            throw new InvalidValueException($"Account ID must be at most {MaxLength} characters.");

        return new AccountId(trimmed);
    }

    public override string ToString() => Value;
}
