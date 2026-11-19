using CashMovements.Domain.Exceptions;

namespace CashMovements.Domain.ValueObjects;

/// <summary>ISO 4217 alphabetic currency code, normalised to upper case (e.g. "ZAR"). Stored as CHAR(3).</summary>
public sealed record Currency
{
    public const int Length = 3;

    private Currency(string code) => Code = code;

    public string Code { get; }

    public static Currency From(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidValueException("Currency is required.");

        var normalised = code.Trim().ToUpperInvariant();
        if (normalised.Length != Length || !normalised.All(char.IsAsciiLetterUpper))
            throw new InvalidValueException($"Currency '{code}' is not a 3-letter ISO 4217 code.");

        return new Currency(normalised);
    }

    public override string ToString() => Code;
}
