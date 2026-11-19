using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;
using CashMovements.Domain.ReadModels;

namespace CashMovements.Application.Interfaces;

/// <summary>Use cases for cash movements: balance, statement, and recording a movement.</summary>
public interface IMovementService
{
    /// <summary>Current balance of one account in one currency. Zero when the account has no movements.</summary>
    /// <exception cref="InvalidValueException">The account ID or currency is invalid.</exception>
    Task<Balance> GetBalanceAsync(
        string accountId,
        string currency,
        CancellationToken cancellationToken);

    /// <summary>
    /// Statement for the period [<paramref name="from"/>, <paramref name="to"/>), streamed: the header with the opening
    /// balance is returned immediately and lines (with running balances) are read as the caller enumerates them.
    /// </summary>
    /// <exception cref="InvalidValueException">The account ID, currency or period is invalid.</exception>
    Task<StatementStream> GetStatementAsync(
        string accountId,
        string currency,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a movement, idempotently per account and external reference. If the reference already exists on the
    /// account with the same amount and currency, the existing movement is returned and nothing is inserted.
    /// </summary>
    /// <returns>The recorded movement (with its database Id), or the existing one on a retry.</returns>
    /// <exception cref="DuplicateMovementException">The reference exists on the account with a different amount or currency.</exception>
    Task<Movement> RecordMovementAsync(
        Movement movement,
        CancellationToken cancellationToken);
}
