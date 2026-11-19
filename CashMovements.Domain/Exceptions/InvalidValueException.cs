namespace CashMovements.Domain.Exceptions;

/// <summary>A value object (account id, currency, external ref, period) was given an invalid value.</summary>
public sealed class InvalidValueException : DomainException
{
    public InvalidValueException(string message)
        : base(message)
    {
    }
}
