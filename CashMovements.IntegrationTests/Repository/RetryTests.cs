using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;
using CashMovements.Domain.ValueObjects;
using CashMovements.Infrastructure.Persistence;
using CashMovements.Infrastructure.Repositories;
using CashMovements.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashMovements.IntegrationTests.Repository;

/// <summary>
/// Retries against real SQL Server: the connection factory's first opens go to an address where nothing is listening
/// (a real connection failure), then it connects normally. Every operation recovers without the caller noticing; if the
/// failures outlast the retries, the caller gets <see cref="StorageUnavailableException"/>.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RetryTests
{
    private const int MaxRetries = 3;
    private static readonly DateTimeOffset OccurredAt = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly Currency Zar = Currency.From("ZAR");

    private readonly SqlServerFixture _db;
    private readonly string _account = "RETRY-" + Guid.NewGuid().ToString("N")[..8];

    public RetryTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task A_read_recovers_after_failed_connections()
    {
        var (repository, connections) = Repository(failFirstOpens: 2);

        var balance = await repository.SumAmountsAsync(AccountId.From(_account), Zar, CancellationToken.None);

        Assert.Equal(0m, balance);
        Assert.Equal(3, connections.Opens);
    }

    [Fact]
    public async Task An_insert_recovers_after_a_failed_connection_and_is_recorded_once()
    {
        var (repository, connections) = Repository(failFirstOpens: 1);

        var recorded = await repository.AddAsync(New("R-1", 25m), CancellationToken.None);

        Assert.True(recorded.Id > 0);
        Assert.Equal(2, connections.Opens);
        Assert.Equal(25m, await Repository(failFirstOpens: 0).Repository.SumAmountsAsync(AccountId.From(_account), Zar, CancellationToken.None));
    }

    [Fact]
    public async Task A_statement_recovers_after_failed_connections()
    {
        await Repository(failFirstOpens: 0).Repository.AddAsync(
            New("S-1", 10m), CancellationToken.None);
        var (repository, connections) = Repository(failFirstOpens: 2);

        await using var statement = await repository.OpenStatementAsync(
            AccountId.From(_account), Zar, OccurredAt.AddDays(-1), OccurredAt.AddDays(1), CancellationToken.None);
        var lines = new List<Movement>();
        await foreach (var movement in statement.Movements)
            lines.Add(movement);

        Assert.Equal("S-1", Assert.Single(lines).ExternalRef.Value);
        Assert.Equal(3, connections.Opens);
    }

    [Fact]
    public async Task Failures_that_outlast_the_retries_are_storage_unavailable()
    {
        var (repository, connections) = Repository(failFirstOpens: int.MaxValue);

        var ex = await Assert.ThrowsAsync<StorageUnavailableException>(
            () => repository.SumAmountsAsync(AccountId.From(_account), Zar, CancellationToken.None));

        Assert.IsType<SqlException>(ex.InnerException);
        Assert.Equal(1 + MaxRetries, connections.Opens);
    }

    private Movement New(string externalRef, decimal amount)
        => Movement.Create(_account, externalRef, amount, "ZAR", OccurredAt, null, TimeProvider.System);

    private (MovementRepository Repository, FlakyConnectionFactory Connections) Repository(int failFirstOpens)
    {
        var connections = new FlakyConnectionFactory(_db.ConnectionString, failFirstOpens);
        var retry = new SqlRetryPolicy(
            new SqlRetryOptions { MaxRetries = MaxRetries, BaseDelay = TimeSpan.Zero, MaxRetryDuration = TimeSpan.FromSeconds(30) },
            NullLogger<SqlRetryPolicy>.Instance,
            TimeProvider.System);

        return (new MovementRepository(connections, retry), connections);
    }

    /// <summary>Fails its first <c>failures</c> opens for real (nothing listens on port 1), then connects normally.</summary>
    private sealed class FlakyConnectionFactory(string connectionString, int failures) : ISqlConnectionFactory
    {
        private static readonly SqlConnectionFactory Unreachable = new(
            "Server=tcp:127.0.0.1,1;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=1");

        private readonly SqlConnectionFactory _real = new(connectionString);
        private int _remainingFailures = failures;

        public int Opens { get; private set; }

        public Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
        {
            Opens++;
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                return Unreachable.OpenAsync(cancellationToken);
            }

            return _real.OpenAsync(cancellationToken);
        }
    }
}
