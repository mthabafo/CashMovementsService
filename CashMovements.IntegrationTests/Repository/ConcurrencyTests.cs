using CashMovements.Application;
using CashMovements.Domain.Entities;
using CashMovements.Domain.ValueObjects;
using CashMovements.Infrastructure.Persistence;
using CashMovements.Infrastructure.Repositories;
using CashMovements.IntegrationTests.Support;
using Microsoft.Data.SqlClient;

namespace CashMovements.IntegrationTests.Repository;

/// <summary>
/// Locking behaviour against real SQL Server: READ_COMMITTED_SNAPSHOT is on, so reads never wait for writes, and a
/// mixed workload (inserts, retries, balances, statements) runs without deadlocks or errors.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ConcurrencyTests
{
    private static readonly Currency Zar = Currency.From("ZAR");

    private readonly SqlServerFixture _db;

    public ConcurrencyTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Read_committed_snapshot_is_on()
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();", connection);

        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Balance_and_ref_lookup_do_not_wait_for_an_uncommitted_insert()
    {
        var account = "PEND-" + Guid.NewGuid().ToString("N")[..8];

        // Another session's insert, not yet committed: it holds an exclusive lock on the new row.
        await using var writer = new SqlConnection(_db.ConnectionString);
        await writer.OpenAsync();
        await using var transaction = writer.BeginTransaction();
        await using (var insert = new SqlCommand(
            $"INSERT INTO dbo.Movement (AccountId, ExternalRef, Amount, Currency, OccurredAt) VALUES (N'{account}', N'PENDING', 100, 'ZAR', SYSDATETIMEOFFSET());",
            writer,
            transaction))
        {
            await insert.ExecuteNonQueryAsync();
        }

        // With locking reads these would wait for that lock and time out after 2 seconds; with READ_COMMITTED_SNAPSHOT
        // they read the last committed state straight away, which doesn't include the pending row yet.
        var repository = new MovementRepository(new SqlConnectionFactory(
            new SqlConnectionStringBuilder(_db.ConnectionString) { CommandTimeout = 2 }.ConnectionString));

        var balance = await repository.SumAmountsAsync(AccountId.From(account), Zar, CancellationToken.None);
        var found = await repository.FindByExternalRefAsync(AccountId.From(account), ExternalRef.From("PENDING"), CancellationToken.None);

        Assert.Equal(0m, balance);
        Assert.Null(found);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task A_mixed_workload_runs_without_deadlocks_or_errors()
    {
        const int writers = 8;
        const int movementsPerWriter = 100;
        string[] accounts = ["CONC-A-" + Guid.NewGuid().ToString("N")[..8], "CONC-B-" + Guid.NewGuid().ToString("N")[..8]];
        var baseTime = DateTimeOffset.UtcNow.AddDays(-10);

        // No retry policy, so a deadlock victim (or any other failure) surfaces and fails the test instead of being retried.
        var repository = new MovementRepository(new SqlConnectionFactory(_db.ConnectionString));
        var service = new MovementService(repository, TimeProvider.System);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Writers: new movements on both accounts, re-submitting every fifth one as a retry.
        var writing = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            await start.Task;
            for (var n = 0; n < movementsPerWriter; n++)
            {
                Movement Submission() => Movement.Create(
                    accounts[n % 2], $"W{w}-{n}", 1m, "ZAR", baseTime.AddSeconds(w * 1000 + n), null, TimeProvider.System);

                var recorded = await service.RecordMovementAsync(Submission(), CancellationToken.None);
                if (n % 5 == 0)
                    Assert.Equal(recorded.Id, (await service.RecordMovementAsync(Submission(), CancellationToken.None)).Id);
            }
        }));

        // Balance readers: every amount is positive and nothing is deleted, so a balance can never go down.
        var balances = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            var last = new decimal[accounts.Length];
            for (var i = 0; i < 100; i++)
            {
                for (var a = 0; a < accounts.Length; a++)
                {
                    var balance = await service.GetBalanceAsync(accounts[a], "ZAR", CancellationToken.None);
                    Assert.True(balance.Amount >= last[a], $"Balance went down from {last[a]} to {balance.Amount}.");
                    last[a] = balance.Amount;
                }
            }
        }));

        // Statement readers: full statements streamed while inserts land, always in (OccurredAt, Id) order.
        var statements = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < 20; i++)
            {
                foreach (var account in accounts)
                {
                    await using var statement = await repository.OpenStatementAsync(
                        AccountId.From(account), Zar, baseTime.AddDays(-1), DateTimeOffset.UtcNow, CancellationToken.None);

                    Movement? previous = null;
                    await foreach (var movement in statement.Movements)
                    {
                        if (previous is not null)
                            Assert.True((movement.OccurredAt, movement.Id).CompareTo((previous.OccurredAt, previous.Id)) > 0);
                        previous = movement;
                    }
                }
            }
        }));

        start.SetResult();
        await Task.WhenAll(writing.Concat(balances).Concat(statements));

        // Every movement counted exactly once, retries included.
        foreach (var account in accounts)
        {
            var balance = await service.GetBalanceAsync(account, "ZAR", CancellationToken.None);
            Assert.Equal(writers * movementsPerWriter / 2m, balance.Amount);
        }
    }
}
