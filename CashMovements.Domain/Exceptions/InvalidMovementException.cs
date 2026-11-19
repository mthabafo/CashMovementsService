namespace CashMovements.Domain.Exceptions;

/// <summary>A movement breaks one of its own rules (zero amount, too many decimals, future date, ...).</summary>
public sealed class InvalidMovementException : DomainException
{
    public InvalidMovementException(string message)
        : base(message)
    {
    }
}
