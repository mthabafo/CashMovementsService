namespace CashMovements.Domain.Exceptions;

/// <summary>
/// The movement store can't be reached right now (connection failure or timeout). Temporary and not the caller's
/// fault, so retrying later is reasonable: the API reports it as <c>Unavailable</c>.
/// <para>
/// Declared here, next to the repository interface it belongs to, so outer layers can handle it without depending on
/// Infrastructure. Deliberately not a <see cref="DomainException"/>, which means invalid input.
/// </para>
/// </summary>
public sealed class StorageUnavailableException : Exception
{
    public StorageUnavailableException(Exception innerException)
        : base("The movement store is unavailable.", innerException)
    {
    }
}
