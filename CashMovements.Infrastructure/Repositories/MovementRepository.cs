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

    public MovementRepository(ISqlConnectionFactory connections)
        => _connections = connections ?? throw new ArgumentNullException(nameof(connections));

    public async Task<Movement?> FindByExternalRefAsync(
        AccountId accountId,
        ExternalRef externalRef,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        try
        {
            var row = await connection.QuerySingleOrDefaultAsync<MovementRow>(Command(
                Procedures.FindByExternalRef,
                new { AccountId = accountId.Value, ExternalRef = externalRef.Value },
                cancellationToken: cancellationToken));

            return row?.ToMovement();
        }
        catch (Exception ex) when (SqlFailures.IsUnavailable(ex, cancellationToken, out var sql))
        {
            throw new StorageUnavailableException(sql);
        }
    }

    public async Task<Movement> AddAsync(Movement movement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movement);

        await using var connection = await _connections.OpenAsync(cancellationToken);

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
                cancellationToken: cancellationToken));

            return row.ToMovement();
        }
        catch (SqlException ex) when (ex.Number is UniqueConstraintViolation or UniqueIndexViolation)
        {
            // Another request recorded this (AccountId, ExternalRef) between the service's lookup and this insert.
            throw new DuplicateMovementException(movement.ExternalRef);
        }
        catch (Exception ex) when (SqlFailures.IsUnavailable(ex, cancellationToken, out var sql))
        {
            // A timeout here leaves the outcome unknown, but retrying is still safe: the external ref makes the insert
            // idempotent, so a retry either inserts once or returns the movement that did get recorded.
            throw new StorageUnavailableException(sql);
        }
    }

    public async Task<decimal> SumAmountsAsync(
        AccountId accountId,
        Currency currency,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        try
        {
            return await SumAsync(connection, transaction: null, accountId, currency, before: null, cancellationToken);
        }
        catch (Exception ex) when (SqlFailures.IsUnavailable(ex, cancellationToken, out var sql))
        {
            throw new StorageUnavailableException(sql);
        }
    }

    public async Task<StatementSource> OpenStatementAsync(
        AccountId accountId,
        Currency currency,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var connection = await _connections.OpenAsync(cancellationToken);
        SqlTransaction? transaction = null;

        try
        {
            // SNAPSHOT: both reads below see the database as it was when the transaction first read data, so the
            // opening balance and the lines always agree, and concurrent inserts are neither blocked nor blocking.
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);

            var opening = await SumAsync(connection, transaction, accountId, currency, from, cancellationToken);

            var movements = ReadInPeriodAsync(connection, transaction, accountId, currency, from, to, cancellationToken);

            return new StatementSource(opening, movements, new Snapshot(connection, transaction));
        }
        catch (Exception ex)
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
            await connection.DisposeAsync();

            if (SqlFailures.IsUnavailable(ex, cancellationToken, out var sql))
                throw new StorageUnavailableException(sql);
            throw;
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
