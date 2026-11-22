using System.Data;
using System.Runtime.CompilerServices;
using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;
using CashMovements.Domain.Interfaces;
using CashMovements.Domain.ReadModels;
using CashMovements.Domain.ValueObjects;
using CashMovements.Infrastructure.Persistence;
using Dapper;
using Microsoft.Data.SqlClient;

namespace CashMovements.Infrastructure.Repositories;

/// <summary>
/// SQL Server implementation of <see cref="IMovementRepository"/>, using Dapper over the stored procedures in
/// 002_procedures.sql. Rows are turned back into domain objects with <see cref="Movement.Rehydrate"/>.
/// </summary>
public sealed class MovementRepository : IMovementRepository
{
    // SQL Server error numbers for a unique constraint / unique index violation.
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    private readonly ISqlConnectionFactory _connections;
    private readonly SqlRetryPolicy _retry;

    /// <param name="connections">Opens connections to the database.</param>
    /// <param name="retry">Retries transient failures; with none supplied, each operation runs once.</param>
    public MovementRepository(ISqlConnectionFactory connections, SqlRetryPolicy? retry = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _retry = retry ?? SqlRetryPolicy.None;
    }

    public Task<Movement?> FindByExternalRefAsync(
        AccountId accountId,
        ExternalRef externalRef,
        CancellationToken cancellationToken)
        => RunAsync(async (connection, token) =>
        {
            var row = await connection.QuerySingleOrDefaultAsync<MovementRow>(Command(
                Procedures.FindByExternalRef,
                new { AccountId = accountId.Value, ExternalRef = externalRef.Value },
                cancellationToken: token));

            return row?.ToMovement();
        }, cancellationToken);

    public Task<Movement> AddAsync(Movement movement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movement);

        // Retrying the insert is safe even when the outcome of a failed attempt is unknown (the connection dropped after
        // SQL Server committed): the retry hits the unique key, becomes DuplicateMovementException, and the service
        // treats that as a re-submission and returns the movement that was recorded. Nothing is counted twice.
        return RunAsync(async (connection, token) =>
        {
            try
            {
                var row = await connection.QuerySingleAsync<MovementRow>(Command(
                    Procedures.Insert,
                    new
                    {
                        AccountId = movement.AccountId.Value,
                        ExternalRef = movement.ExternalRef.Value,
                        movement.Amount,
                        Currency = movement.Currency.Code,
                        movement.OccurredAt,
                        movement.Narration,
                    },
                    cancellationToken: token));

                return row.ToMovement();
            }
            catch (SqlException ex) when (ex.Number is UniqueConstraintViolation or UniqueIndexViolation)
            {
                // Another request (or an earlier attempt of this one) already recorded this (AccountId, ExternalRef).
                throw new DuplicateMovementException(movement.ExternalRef);
            }
        }, cancellationToken);
    }

    public Task<decimal> SumAmountsAsync(
        AccountId accountId,
        Currency currency,
        CancellationToken cancellationToken)
        => RunAsync(
            (connection, token) => SumAsync(connection, transaction: null, accountId, currency, before: null, token),
            cancellationToken);

    public async Task<StatementSource> OpenStatementAsync(
        AccountId accountId,
        Currency currency,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        try
        {
            // Only this part is retried: no rows have been read yet, so a retry simply starts a fresh snapshot. Once
            // lines are streaming they can't be retried (the caller already has some of them); see ReadInPeriodAsync.
            return await _retry.ExecuteAsync(async token =>
            {
                var connection = await _connections.OpenAsync(token);
                SqlTransaction? transaction = null;

                try
                {
                    // SNAPSHOT: both reads below see the database as it was when the transaction first read data, so
                    // the opening balance and the lines always agree, and concurrent inserts are neither blocked nor
                    // blocking.
                    transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Snapshot, token);

                    var opening = await SumAsync(connection, transaction, accountId, currency, from, token);

                    // The caller's token, not the attempt's: the lines are read after this method has returned.
                    var movements = ReadInPeriodAsync(connection, transaction, accountId, currency, from, to, cancellationToken);

                    // Counting (only when asked, for an export) runs on this connection and transaction before the
                    // lines are read, so it sees the same snapshot and always matches them.
                    return new StatementSource(
                        opening,
                        movements,
                        new Snapshot(connection, transaction),
                        countToken => CountInPeriodAsync(connection, transaction, accountId, currency, from, to, countToken));
                }
                catch
                {
                    if (transaction is not null)
                        await transaction.DisposeAsync();
                    await connection.DisposeAsync();
                    throw;
                }
            }, cancellationToken);
        }
        catch (Exception ex) when (SqlFailures.IsUnavailable(ex, cancellationToken, out var sql))
        {
            throw new StorageUnavailableException(sql);
        }
    }

    /// <summary>
    /// Runs one self-contained operation (open a connection, run a command), retrying transient failures. What's left
    /// once retries run out is reported as <see cref="StorageUnavailableException"/> if it means the store is
    /// unavailable; anything else (a duplicate, a corrupt row, a bug) passes through unchanged.
    /// </summary>
    private async Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _retry.ExecuteAsync(async token =>
            {
                await using var connection = await _connections.OpenAsync(token);
                return await operation(connection, token);
            }, cancellationToken);
        }
        catch (Exception ex) when (SqlFailures.IsUnavailable(ex, cancellationToken, out var sql))
        {
            throw new StorageUnavailableException(sql);
        }
    }

    private static async Task<int> CountInPeriodAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AccountId accountId,
        Currency currency,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        try
        {
            return await connection.ExecuteScalarAsync<int>(Command(
                Procedures.CountInPeriod,
                new { AccountId = accountId.Value, Currency = currency.Code, From = from, To = to },
                transaction,
                cancellationToken));
        }
        catch (Exception ex) when (SqlFailures.IsUnavailable(ex, cancellationToken, out var sql))
        {
            // Not retried: it runs inside the statement's snapshot, which a retry couldn't reopen.
            throw new StorageUnavailableException(sql);
        }
    }

    private static async Task<decimal> SumAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        AccountId accountId,
        Currency currency,
        DateTimeOffset? before,
        CancellationToken cancellationToken)
        => await connection.ExecuteScalarAsync<decimal>(Command(
            Procedures.SumAmounts,
            new { AccountId = accountId.Value, Currency = currency.Code, Before = before },
            transaction,
            cancellationToken));

    private static async IAsyncEnumerable<Movement> ReadInPeriodAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AccountId accountId,
        Currency currency,
        DateTimeOffset from,
        DateTimeOffset to,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Unbuffered: each row is materialised as it arrives and handed straight to the caller.
        var rows = connection.QueryUnbufferedAsync<MovementRow>(
            Procedures.ListInPeriod,
            new { AccountId = accountId.Value, Currency = currency.Code, From = from, To = to },
            transaction,
            commandType: CommandType.StoredProcedure);

        // Enumerated by hand because C# doesn't allow "yield return" inside a try block that has a catch: only reading
        // the next row is guarded, so a connection lost part-way through a statement is reported as unavailable too.
        // Not retried: the caller has already received the earlier lines, and a new snapshot could differ.
        await using var enumerator = rows.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await enumerator.MoveNextAsync())
                    yield break;
            }
            catch (Exception ex) when (SqlFailures.IsUnavailable(ex, cancellationToken, out var sql))
            {
                throw new StorageUnavailableException(sql);
            }

            yield return enumerator.Current.ToMovement();

            // Stop promptly (with OperationCanceledException) when the caller cancels between rows.
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static CommandDefinition Command(
        string procedure,
        object parameters,
        SqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        => new(procedure, parameters, transaction, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken);

    private static class Procedures
    {
        public const string FindByExternalRef = "dbo.usp_Movement_FindByExternalRef";
        public const string Insert = "dbo.usp_Movement_Insert";
        public const string SumAmounts = "dbo.usp_Movement_SumAmounts";
        public const string ListInPeriod = "dbo.usp_Movement_ListInPeriod";
        public const string CountInPeriod = "dbo.usp_Movement_CountInPeriod";
    }

    /// <summary>One row of dbo.Movement, exactly as the procedures return it.</summary>
    private sealed class MovementRow
    {
        public long Id { get; init; }
        public string AccountId { get; init; } = "";
        public string ExternalRef { get; init; } = "";
        public decimal Amount { get; init; }
        public string Currency { get; init; } = "";
        public DateTimeOffset OccurredAt { get; init; }
        public string? Narration { get; init; }

        /// <exception cref="CorruptMovementDataException">The row breaks a domain rule.</exception>
        public Movement ToMovement()
        {
            try
            {
                return Movement.Rehydrate(Id, AccountId, ExternalRef, Amount, Currency, OccurredAt, Narration);
            }
            catch (Exception ex) when (ex is DomainException or ArgumentException)
            {
                // A bad row is a server-side data problem, not the caller's mistake: don't let it surface as a
                // DomainException (which the API reports as InvalidArgument).
                throw new CorruptMovementDataException(Id, ex);
            }
        }
    }

    /// <summary>The connection and read-only SNAPSHOT transaction behind one statement.</summary>
    private sealed class Snapshot(SqlConnection connection, SqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Read-only, so there is nothing to commit: disposing rolls back and returns the connection to the pool.
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
