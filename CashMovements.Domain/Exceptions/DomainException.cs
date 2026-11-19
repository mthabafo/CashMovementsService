namespace CashMovements.Domain.Exceptions;

/// <summary>Base type for every rule violation raised by the domain.</summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message)
        : base(message)
    {
    }
}
