namespace CashMovements.Infrastructure.Persistence;

/// <summary>
/// A stored movement breaks the domain's rules, so it can't be turned back into a <c>Movement</c>.
/// <para>
/// Deliberately <b>not</b> a <c>DomainException</c>: the caller's input is fine, the stored data is not. The gRPC layer
/// therefore treats it as an unexpected server error (logged, returned as <c>Internal</c>) instead of blaming the caller
/// with <c>InvalidArgument</c>. The original domain error is the <see cref="Exception.InnerException"/>.
/// </para>
/// </summary>
public sealed class CorruptMovementDataException : Exception
{
    public CorruptMovementDataException(long movementId, Exception inner)
        : base($"Stored movement {movementId} is invalid and can't be read: {inner.Message}", inner)
        => MovementId = movementId;

    /// <summary>The database Id of the offending row (dbo.Movement.Id).</summary>
    public long MovementId { get; }
}
