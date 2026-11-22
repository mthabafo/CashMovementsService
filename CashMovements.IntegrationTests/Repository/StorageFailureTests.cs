using CashMovements.Domain.Exceptions;
using CashMovements.Domain.ValueObjects;
using CashMovements.Infrastructure.Persistence;
using CashMovements.Infrastructure.Repositories;
using CashMovements.IntegrationTests.Support;
using Microsoft.Data.SqlClient;

namespace CashMovements.IntegrationTests.Repository;

/// <summary>
/// Real SQL Server failures the repository must report as <see cref="StorageUnavailableException"/> (temporary, the API
/// says UNAVAILABLE) rather than letting a raw <see cref="SqlException"/> through (the API would say INTERNAL), and the
/// one it must not: a caller cancelling is a cancellation, not an outage.
/// <para>The mid-stream test uses KILL, so the test login needs the processadmin or sysadmin role.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class StorageFailureTests
{
    private static readonly AccountId Account = AccountId.From("STORAGE-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly Currency Zar = Currency.From("ZAR");

    private readonly SqlServerFixture _db;

    public StorageFailureTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task A_query_that_times_out_is_storage_unavailable()
    {
        await using var blocker = await LockMovementTableAsync();
        var repository = Repository(new SqlConnectionStringBuilder(_db.ConnectionString) { CommandTimeout = 1 });

        var ex = await Assert.ThrowsAsync<StorageUnavailableException>(
            () => repository.SumAmountsAsync(Account, Zar, CancellationToken.None));

        Assert.Equal(-2, Assert.IsType<SqlException>(ex.InnerException).Number); // SQL Server's timeout
    }

    [Fact]
    public async Task A_query_cancelled_by_the_caller_is_not_storage_unavailable()
    {
        await using var blocker = await LockMovementTableAsync();
        var repository = Repository(new SqlConnectionStringBuilder(_db.ConnectionString) { CommandTimeout = 30 });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var ex = await Record.ExceptionAsync(() => repository.SumAmountsAsync(Account, Zar, cancellation.Token));

        Assert.NotNull(ex);
        Assert.IsNotType<StorageUnavailableException>(ex);
    }

    [Fact]
    public async Task Losing_the_connection_part_way_through_a_statement_is_storage_unavailable()
    {
        // Enough rows that SQL Server is still sending when the connection is killed.
        var account = AccountId.From("KILL-" + Guid.NewGuid().ToString("N")[..8]);
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await _db.ExecuteAsync($"""
            WITH numbers AS (
                SELECT TOP (50000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO dbo.Movement (AccountId, ExternalRef, Amount, Currency, OccurredAt)
            SELECT N'{account}', CONCAT(N'K-', n), 1, 'ZAR', DATEADD(SECOND, CAST(n AS INT), CAST('2026-01-01T00:00:00+00:00' AS DATETIMEOFFSET(7)))
            FROM numbers;
            """);

        // A unique application name identifies the statement's session so it can be killed.
        var applicationName = "cashmovements-kill-" + Guid.NewGuid().ToString("N");
        var repository = Repository(new SqlConnectionStringBuilder(_db.ConnectionString)
        {
            ApplicationName = applicationName,
            Pooling = false,
        });

        await using var statement = await repository.OpenStatementAsync(account, Zar, from, from.AddDays(2), CancellationToken.None);
        await using var movements = statement.Movements.GetAsyncEnumerator();
        Assert.True(await movements.MoveNextAsync());

        await _db.ExecuteAsync($"""
            DECLARE @session INT = (SELECT TOP (1) session_id FROM sys.dm_exec_sessions WHERE program_name = N'{applicationName}');
            DECLARE @kill NVARCHAR(50) = CONCAT(N'KILL ', @session);
            EXEC (@kill);
            """);

        var ex = await Assert.ThrowsAsync<StorageUnavailableException>(async () =>
        {
            while (await movements.MoveNextAsync())
            {
            }
        });

        Assert.IsType<SqlException>(ex.InnerException);
    }

    private static MovementRepository Repository(SqlConnectionStringBuilder connectionString)
        => new(new SqlConnectionFactory(connectionString.ConnectionString));

    /// <summary>Holds an exclusive lock on dbo.Movement until disposed, so any other read or write waits.</summary>
    private async Task<IAsyncDisposable> LockMovementTableAsync()
    {
        var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        var transaction = connection.BeginTransaction();
        await using (var command = new SqlCommand("SELECT TOP (0) 1 FROM dbo.Movement WITH (TABLOCKX, HOLDLOCK);", connection, transaction))
            await command.ExecuteNonQueryAsync();

        return new TableLock(connection, transaction);
    }

    private sealed class TableLock(SqlConnection connection, SqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
