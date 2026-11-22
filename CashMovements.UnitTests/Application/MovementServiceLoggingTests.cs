using System.Runtime.CompilerServices;
using CashMovements.Application;
using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;
using CashMovements.Domain.Interfaces;
using CashMovements.Domain.ReadModels;
using CashMovements.Domain.ValueObjects;
using CashMovements.UnitTests.Support;
using Microsoft.Extensions.Logging;

namespace CashMovements.UnitTests.Application;

/// <summary>
/// The business events <see cref="MovementService"/> logs: which event, at what level, with which structured fields,
/// and never the raw account ID, an amount or a narration.
/// </summary>
public class MovementServiceLoggingTests
{
    private const string Account = "ACCOUNT-1001";
    private const string MaskedAccount = "****1001";

    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider Clock = new FixedClock(Now);

    private readonly InMemoryMovementRepository _repository = new();
    private readonly CapturingLogger<MovementService> _logger = new();
    private readonly MovementService _service;

    public MovementServiceLoggingTests() => _service = new MovementService(_repository, Clock, _logger);

    private static Movement New(decimal amount, string currency = "ZAR", int daysAgo = 1)
        => Movement.Create(Account, "REF-1", amount, currency, Now.AddDays(-daysAgo), "Rent for November", Clock);

    [Fact]
    public async Task Recording_logs_MovementRecorded_with_masked_account_and_no_amount()
    {
        var recorded = await _service.RecordMovementAsync(New(1234.56m), default);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(1001, entry.EventId.Id);
        Assert.Equal("MovementRecorded", entry.EventId.Name);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(recorded.Id, entry.Fields["MovementId"]);
        Assert.Equal(MaskedAccount, entry.Fields["Account"]);
        Assert.Equal("REF-1", entry.Fields["ExternalRef"]);
        Assert.Equal("ZAR", entry.Fields["Currency"]);
        AssertNothingSensitive(entry);
    }

    [Fact]
    public async Task A_retry_logs_MovementReplayed_with_the_original_id()
    {
        var original = await _service.RecordMovementAsync(New(100m), default);

        await _service.RecordMovementAsync(New(100m), default);

        var entry = _logger.Entries.Last();
        Assert.Equal(1002, entry.EventId.Id);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(original.Id, entry.Fields["MovementId"]);
        Assert.Equal(false, entry.Fields["LostInsertRace"]);
        AssertNothingSensitive(entry);
    }

    [Fact]
    public async Task Losing_the_insert_race_to_an_identical_request_logs_MovementReplayed_with_LostInsertRace()
    {
        _repository.SimulateConcurrentInsert(Movement.Rehydrate(42, Account, "REF-1", 100m, "ZAR", Now.AddDays(-1), null));

        await _service.RecordMovementAsync(New(100m), default);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(1002, entry.EventId.Id);
        Assert.Equal(42L, entry.Fields["MovementId"]);
        Assert.Equal(true, entry.Fields["LostInsertRace"]);
    }

    [Theory]
    [InlineData(200, "ZAR", "amount")]
    [InlineData(100, "USD", "currency")]
    [InlineData(200, "USD", "amount and currency")]
    public async Task A_conflict_logs_a_MovementConflict_warning_naming_the_fields(int amount, string currency, string fields)
    {
        await _service.RecordMovementAsync(New(100m), default);

        await Assert.ThrowsAsync<DuplicateMovementException>(() => _service.RecordMovementAsync(New(amount, currency), default));

        var entry = _logger.Entries.Last();
        Assert.Equal(1003, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(fields, entry.Fields["ConflictingFields"]);
        Assert.Equal(MaskedAccount, entry.Fields["Account"]);
        AssertNothingSensitive(entry);
    }

    [Fact]
    public async Task A_finished_statement_logs_StatementCompleted_with_the_line_count()
    {
        _repository.Seed(Movement.Rehydrate(1, Account, "R1", 10m, "ZAR", Now.AddDays(-3), null));
        _repository.Seed(Movement.Rehydrate(2, Account, "R2", -4m, "ZAR", Now.AddDays(-2), null));

        await using (var statement = await _service.GetStatementAsync(Account, "ZAR", Now.AddDays(-10), Now, default))
        {
            await foreach (var _ in statement.Lines)
            {
            }
        }

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(1101, entry.EventId.Id);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(2, entry.Fields["LineCount"]);
        Assert.Equal(MaskedAccount, entry.Fields["Account"]);
        AssertNothingSensitive(entry);
    }

    [Fact]
    public async Task A_statement_whose_reader_stops_early_logs_StatementEndedEarly_stopped()
    {
        _repository.Seed(Movement.Rehydrate(1, Account, "R1", 10m, "ZAR", Now.AddDays(-3), null));
        _repository.Seed(Movement.Rehydrate(2, Account, "R2", -4m, "ZAR", Now.AddDays(-2), null));

        await using (var statement = await _service.GetStatementAsync(Account, "ZAR", Now.AddDays(-10), Now, default))
        {
            await foreach (var _ in statement.Lines)
                break; // e.g. the client disconnected after the first line
        }

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(1102, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("stopped", entry.Fields["Reason"]);
        Assert.Equal(1, entry.Fields["LineCount"]);
    }

    [Fact]
    public async Task A_statement_that_fails_part_way_logs_StatementEndedEarly_failed()
    {
        var service = new MovementService(new FailingAfterOneRowRepository(), Clock, _logger);

        await using (var statement = await service.GetStatementAsync(Account, "ZAR", Now.AddDays(-10), Now, default))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in statement.Lines)
                {
                }
            });
        }

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(1102, entry.EventId.Id);
        Assert.Equal("failed", entry.Fields["Reason"]);
        Assert.Equal(1, entry.Fields["LineCount"]);
    }

    private static void AssertNothingSensitive(LogEntry entry)
    {
        Assert.DoesNotContain(Account, entry.AllText);
        Assert.DoesNotContain("Rent for November", entry.AllText);
        Assert.DoesNotContain("1234", entry.AllText);
        Assert.False(entry.Fields.ContainsKey("Amount"));
    }

    /// <summary>Yields one movement, then fails as a lost connection would.</summary>
    private sealed class FailingAfterOneRowRepository : IMovementRepository
    {
        public Task<StatementSource> OpenStatementAsync(AccountId accountId, Currency currency, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
            => Task.FromResult(new StatementSource(0m, Rows()));

        private static async IAsyncEnumerable<Movement> Rows([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Movement.Rehydrate(1, Account, "R1", 10m, "ZAR", Now.AddDays(-3), null);
            await Task.Yield();
            throw new InvalidOperationException("connection lost");
        }

        public Task<Movement?> FindByExternalRefAsync(AccountId accountId, ExternalRef externalRef, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Movement> AddAsync(Movement movement, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<decimal> SumAmountsAsync(AccountId accountId, Currency currency, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
