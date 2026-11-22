using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;
using CashMovements.Domain.ValueObjects;
using CashMovements.Infrastructure.Persistence;
using CashMovements.Infrastructure.Repositories;
using CashMovements.IntegrationTests.Support;

namespace CashMovements.IntegrationTests.Repository;

/// <summary>
/// <see cref="MovementRepository"/> against a real SQL Server built from the repo's scripts.
/// Every test uses its own random account, so tests don't see each other's rows.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class MovementRepositoryTests
{
    // Fixed instant in the past: Movement.Create rejects future dates against the system clock.
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly Currency Zar = Currency.From("ZAR");

    private readonly SqlServerFixture _db;
    private readonly MovementRepository _repository;
    private readonly string _account = NewAccount();

    public MovementRepositoryTests(SqlServerFixture db)
    {
        _db = db;
        _repository = new MovementRepository(new SqlConnectionFactory(db.ConnectionString));
    }

    private AccountId Account => AccountId.From(_account);

    private static string NewAccount() => "T" + Guid.NewGuid().ToString("N"); // 33 chars, max is 34

    private static Movement New(
        string account,
        string externalRef,
        decimal amount,
        DateTimeOffset occurredAt,
        string currency = "ZAR",
        string? narration = null)
        => Movement.Create(account, externalRef, amount, currency, occurredAt, narration, TimeProvider.System);

    private Task<Movement> Add(string externalRef, decimal amount, DateTimeOffset occurredAt, string currency = "ZAR")
        => _repository.AddAsync(New(_account, externalRef, amount, occurredAt, currency), default);

    // ---------- AddAsync / FindByExternalRefAsync ----------

    [Fact]
    public async Task Add_assigns_an_id_and_every_field_round_trips()
    {
        var occurred = new DateTimeOffset(2026, 3, 1, 14, 30, 15, 123, TimeSpan.FromHours(2)).AddTicks(4567);

        var added = await _repository.AddAsync(New(_account, "REF-1", -1234.56m, occurred, narration: "Rent"), default);
        var found = await _repository.FindByExternalRefAsync(Account, ExternalRef.From("REF-1"), default);

        Assert.True(added.Id > 0);
        Assert.NotNull(found);
        Assert.Equal(added.Id, found.Id);
        Assert.Equal(_account, found.AccountId.Value);
        Assert.Equal("REF-1", found.ExternalRef.Value);
        Assert.Equal(-1234.56m, found.Amount);
        Assert.Equal("ZAR", found.Currency.Code);
        Assert.Equal(occurred, found.OccurredAt);              // same instant, to the tick
        Assert.Equal(TimeSpan.Zero, found.OccurredAt.Offset);  // returned as UTC
        Assert.Equal("Rent", found.Narration);
    }

    [Fact]
    public async Task Add_without_narration_round_trips_null()
    {
        await Add("REF-1", 10m, T0);

        var found = await _repository.FindByExternalRefAsync(Account, ExternalRef.From("REF-1"), default);

        Assert.Null(found!.Narration);
    }

    [Fact]
    public async Task Find_returns_null_when_there_is_no_such_movement()
        => Assert.Null(await _repository.FindByExternalRefAsync(Account, ExternalRef.From("NOPE"), default));

    [Fact]
    public async Task External_refs_are_unique_per_account_not_globally()
    {
        var other = NewAccount();
        await Add("REF-1", 10m, T0);
        await _repository.AddAsync(New(other, "REF-1", 20m, T0), default);

        var mine = await _repository.FindByExternalRefAsync(Account, ExternalRef.From("REF-1"), default);
        var theirs = await _repository.FindByExternalRefAsync(AccountId.From(other), ExternalRef.From("REF-1"), default);

        Assert.Equal(10m, mine!.Amount);
        Assert.Equal(20m, theirs!.Amount);
    }

    [Fact]
    public async Task Account_ids_and_external_refs_are_case_sensitive()
    {
        var lower = "t" + Guid.NewGuid().ToString("N");
        var upper = lower.ToUpperInvariant();

        await _repository.AddAsync(New(lower, "ref-a", 1m, T0), default);
        await _repository.AddAsync(New(upper, "ref-a", 2m, T0), default); // different account
        await _repository.AddAsync(New(lower, "REF-A", 3m, T0), default); // different ref

        Assert.Equal(1m, (await _repository.FindByExternalRefAsync(AccountId.From(lower), ExternalRef.From("ref-a"), default))!.Amount);
        Assert.Equal(2m, (await _repository.FindByExternalRefAsync(AccountId.From(upper), ExternalRef.From("ref-a"), default))!.Amount);
        Assert.Equal(3m, (await _repository.FindByExternalRefAsync(AccountId.From(lower), ExternalRef.From("REF-A"), default))!.Amount);
    }

    [Fact]
    public async Task Adding_the_same_ref_twice_on_an_account_is_a_duplicate()
    {
        await Add("REF-1", 10m, T0);

        var ex = await Assert.ThrowsAsync<DuplicateMovementException>(() => Add("REF-1", 10m, T0));

        Assert.Equal("REF-1", ex.ExternalRef.Value);
    }

    // ---------- SumAmountsAsync ----------

    [Fact]
    public async Task Sum_of_an_account_without_movements_is_zero()
        => Assert.Equal(0m, await _repository.SumAmountsAsync(Account, Zar, default));

    [Fact]
    public async Task Sum_adds_signed_amounts_for_that_account_and_currency_only()
    {
        await Add("R1", 1000m, T0);
        await Add("R2", -149.50m, T0.AddDays(1));
        await Add("R3", 0.01m, T0.AddDays(2));
        await Add("R4", 999m, T0, currency: "USD");
        await _repository.AddAsync(New(NewAccount(), "R1", 555m, T0), default);

        Assert.Equal(850.51m, await _repository.SumAmountsAsync(Account, Zar, default));
    }

    // ---------- OpenStatementAsync ----------

    [Fact]
    public async Task Statement_has_opening_balance_and_lines_in_the_half_open_period_in_order()
    {
        var from = T0;
        var to = T0.AddDays(10);
        await Add("BEFORE", 1000m, from.AddTicks(-1));      // just before the period: opening balance
        await Add("START", 1m, from);                        // on the start: included
        await Add("TIE-B", 3m, from.AddDays(5));             // same instant as TIE-C: ordered by Id
        await Add("TIE-C", 4m, from.AddDays(5));
        await Add("EARLY", 2m, from.AddDays(1));             // inserted later, occurred earlier
        await Add("END", 99m, to);                           // on the end: excluded
        await Add("USD", 50m, from.AddDays(2), currency: "USD");

        await using var statement = await _repository.OpenStatementAsync(Account, Zar, from, to, default);
        var refs = await RefsAsync(statement.Movements);

        Assert.Equal(1000m, statement.OpeningBalance);
        Assert.Equal(new[] { "START", "EARLY", "TIE-B", "TIE-C" }, refs);
    }

    [Fact]
    public async Task Statement_reads_from_one_snapshot_so_later_inserts_are_not_seen()
    {
        await Add("BEFORE", 100m, T0.AddDays(-1));
        await Add("IN-1", 10m, T0.AddDays(1));

        await using var statement = await _repository.OpenStatementAsync(Account, Zar, T0, T0.AddDays(10), default);

        // Committed by other connections after the snapshot was taken, both before and inside the period.
        await Add("LATE-BEFORE", 5000m, T0.AddDays(-2));
        await Add("LATE-IN", 20m, T0.AddDays(2));

        var refs = await RefsAsync(statement.Movements);

        Assert.Equal(100m, statement.OpeningBalance);
        Assert.Equal(new[] { "IN-1" }, refs);
    }

    [Fact]
    public async Task The_line_count_comes_from_the_statement_snapshot_so_it_always_matches_the_lines()
    {
        await Add("IN-1", 10m, T0.AddDays(1));
        await Add("IN-2", 20m, T0.AddDays(2));
        await Add("AFTER", 30m, T0.AddDays(20)); // outside the period

        await using var statement = await _repository.OpenStatementAsync(Account, Zar, T0, T0.AddDays(10), default);

        // Committed by another connection after the snapshot was taken, inside the period: an export's progress total
        // must not count it, because the lines won't include it.
        await Add("LATE-IN", 40m, T0.AddDays(3));

        var count = await statement.CountLinesAsync(default);
        var refs = await RefsAsync(statement.Movements);

        Assert.Equal(2, count);
        Assert.Equal(new[] { "IN-1", "IN-2" }, refs);
    }

    [Fact]
    public async Task Statement_with_no_movements_in_the_period_is_empty()
    {
        await using var statement = await _repository.OpenStatementAsync(Account, Zar, T0, T0.AddDays(1), default);

        Assert.Equal(0m, statement.OpeningBalance);
        Assert.Empty(await RefsAsync(statement.Movements));
    }

    [Fact]
    public async Task Statement_streams_thousands_of_rows_in_order()
    {
        const int rows = 5000;
        await InsertManyAsync(rows);

        await using var statement = await _repository.OpenStatementAsync(Account, Zar, T0, T0.AddDays(1), default);

        var count = 0;
        Movement? previous = null;
        await foreach (var movement in statement.Movements)
        {
            if (previous is not null)
                Assert.True(movement.OccurredAt > previous.OccurredAt
                    || (movement.OccurredAt == previous.OccurredAt && movement.Id > previous.Id));
            previous = movement;
            count++;
        }

        Assert.Equal(rows, count);
    }

    [Fact]
    public async Task Cancelling_part_way_stops_the_stream_and_the_snapshot_can_be_released()
    {
        await InsertManyAsync(2000);
        using var cts = new CancellationTokenSource();

        var statement = await _repository.OpenStatementAsync(Account, Zar, T0, T0.AddDays(1), default);
        var read = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in statement.Movements.WithCancellation(cts.Token))
            {
                if (++read == 10)
                    cts.Cancel();
            }
        });
        await statement.DisposeAsync();

        Assert.Equal(10, read);
        // The database is still usable afterwards.
        Assert.Equal(2000m, await _repository.SumAmountsAsync(Account, Zar, default));
    }

    // ---------- Helpers ----------

    private static async Task<List<string>> RefsAsync(IAsyncEnumerable<Movement> movements)
    {
        var refs = new List<string>();
        await foreach (var movement in movements)
            refs.Add(movement.ExternalRef.Value);
        return refs;
    }

    /// <summary>Set-based insert of <paramref name="count"/> R1.00 movements, a second apart (some sharing a second).</summary>
    private Task InsertManyAsync(int count)
        => _db.ExecuteAsync($"""
            INSERT INTO dbo.Movement (AccountId, ExternalRef, Amount, Currency, OccurredAt)
            SELECT TOP ({count}) N'{_account}', CONCAT(N'BULK-', n), 1.00, 'ZAR',
                   DATEADD(SECOND, n / 2, CAST('{T0:yyyy-MM-ddTHH:mm:ss}+00:00' AS DATETIMEOFFSET(7)))
            FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                  FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS numbers;
            """);
}
