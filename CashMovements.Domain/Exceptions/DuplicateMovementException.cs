using CashMovements.Domain.ValueObjects;

namespace CashMovements.Domain.Exceptions;

/// <summary>
/// A movement arrived with an <see cref="ExternalRef"/> that is already recorded with a different payload.
/// (The same ref with the same payload is an idempotent replay, not an error.)
/// </summary>
public sealed class DuplicateMovementException : DomainException
{
    public DuplicateMovementException(ExternalRef externalRef)
        : base($"External reference '{externalRef}' is already recorded with different details.")
    {
        ExternalRef = externalRef;
    }

    public ExternalRef ExternalRef { get; }
}
