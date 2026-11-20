using System.Diagnostics;
using System.Globalization;
using CashMovements.IntegrationTests.Support;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Xunit.Abstractions;
using Proto = CashMovements.Grpc.Contracts;
using ItemCase = CashMovements.Grpc.Contracts.GetStatementResponse.ItemOneofCase;

namespace CashMovements.IntegrationTests.Volume;

/// <summary>
/// Volume: a statement for a busy account (150,000 lines, plus 50,000 earlier movements behind the opening balance)
/// streamed end to end: generated gRPC client → real host → service → MovementRepository → SQL Server.
/// <para>
/// Checks that every line arrives, in order, with the right running balance, and that the statement is streamed rather
/// than buffered: the first line arrives quickly and live memory stays flat however many lines have been read.
/// Timings and memory are written to the test output.
/// </para>
/// Run only these: <c>dotnet test --filter Category=Volume</c>. Skip them: <c>dotnet test --filter Category!=Volume</c>.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Volume")]
public sealed class StatementVolumeTests : IDisposable
{
    private const int Lines = 150_000;
    private const int EarlierMovements = 50_000;
    private const decimal EarlierAmount = 10.00m;

    // Limits are deliberately generous so the test fails on a design regression, not on a slow or busy machine.
    // Measured on a developer machine (local SQL Server): first line ~20 ms, all 150,000 lines ~1.8 s.
    private static readonly TimeSpan MaxTimeToFirstLine = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxTotalTime = TimeSpan.FromSeconds(60);

    // Measured growth while streaming: ~0.1 MB. With the server deliberately changed to read every line into memory
    // before sending (the regression this guards against), growth was ~52 MB and this test failed.
    private const long MaxLiveMemoryGrowthBytes = 20 * 1024 * 1024;
    private const int MemorySampleEvery = 25_000;

    // A fixed period in the past (Movement rules reject future dates); lines are 0.5 s apart on average.
    private static readonly DateTimeOffset From = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(3);

    private readonly SqlServerFixture _db;
    private readonly ITestOutputHelper _output;
    private readonly GrpcTestHost _host;
    private readonly string _account = "V" + Guid.NewGuid().ToString("N");

    public StatementVolumeTests(SqlServerFixture db, ITestOutputHelper output)
    {
        _db = db;
        _output = output;
        _host = new GrpcTestHost(db.ConnectionString);
    }

    public void Dispose() => _host.Dispose();

    /// <summary>Amount of line <paramref name="n"/> (1-based): between -499.50 and +499.50, never zero.</summary>
    private static decimal LineAmount(int n) => (n % 1000) - 499.5m;

    [Fact]
    public async Task Statement_with_150000_lines_streams_completely_in_order_with_flat_memory()
    {
        var seeding = Stopwatch.StartNew();
        await SeedAsync();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Seeded {EarlierMovements:N0} earlier movements and {Lines:N0} lines in {seeding.Elapsed.TotalSeconds:F1} s."));

        var expectedOpening = EarlierMovements * EarlierAmount;
        var expectedRunning = expectedOpening;

        // Warm up the same code path (JIT, connection and buffer pools) with a one-line statement, then take the
        // memory baseline before the big call. Anything the server or client held on to for the statement, including
        // buffering it all before sending, then shows up as growth.
        await WarmUpAsync();
        var baselineMemory = LiveMemory();
        var peakMemory = baselineMemory;

        var total = Stopwatch.StartNew();
        TimeSpan? timeToHeader = null, timeToFirstLine = null;
        var lineCount = 0;
        Proto.StatementFooter? footer = null;
        var previousOccurredAt = DateTimeOffset.MinValue;
        long previousId = 0;

        using var call = _host.Client.GetStatement(new Proto.GetStatementRequest
        {
            AccountId = _account,
            Currency = "ZAR",
            From = Timestamp.FromDateTimeOffset(From),
            To = Timestamp.FromDateTimeOffset(To),
        });

        await foreach (var item in call.ResponseStream.ReadAllAsync())
        {
            switch (item.ItemCase)
            {
                case ItemCase.Header:
                    timeToHeader = total.Elapsed;
                    Assert.Equal(Format(expectedOpening), item.Header.OpeningBalance);
                    break;

                case ItemCase.Line:
                    lineCount++;
                    if (lineCount == 1)
                    {
                        timeToFirstLine = total.Elapsed;
                        var live = LiveMemory();
                        peakMemory = Math.Max(peakMemory, live);
                        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {lineCount,7:N0} line   {total.Elapsed.TotalSeconds,6:F1} s  live memory {Mb(live)}"));
                    }

                    // Line n is the movement inserted n-th: proves nothing is missing, duplicated or out of order.
                    var movement = item.Line.Movement;
                    Assert.Equal($"VOL-{lineCount}", movement.ExternalRef);

                    // Statement order: OccurredAt, then Id for movements at the same instant (every pair shares one).
                    var occurredAt = movement.OccurredAt.ToDateTimeOffset();
                    Assert.True(
                        occurredAt > previousOccurredAt || (occurredAt == previousOccurredAt && movement.Id > previousId),
                        $"Line {lineCount} is out of order.");
                    previousOccurredAt = occurredAt;
                    previousId = movement.Id;

                    expectedRunning += LineAmount(lineCount);
                    Assert.Equal(Format(expectedRunning), item.Line.RunningBalance);

                    if (lineCount % MemorySampleEvery == 0)
                    {
                        var live = LiveMemory();
                        peakMemory = Math.Max(peakMemory, live);
                        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {lineCount,7:N0} lines  {total.Elapsed.TotalSeconds,6:F1} s  live memory {Mb(live)}"));
                    }
                    break;

                case ItemCase.Footer:
                    footer = item.Footer;
                    break;
            }
        }

        total.Stop();
        var growth = peakMemory - baselineMemory;

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Header after      {timeToHeader?.TotalMilliseconds:F0} ms"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"First line after  {timeToFirstLine?.TotalMilliseconds:F0} ms"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"All {lineCount:N0} lines in {total.Elapsed.TotalSeconds:F1} s ({lineCount / total.Elapsed.TotalSeconds:N0} lines/s)"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Live memory before the call {Mb(baselineMemory)}, peak while streaming {Mb(peakMemory)}, growth {Mb(growth)}"));

        Assert.Equal(Lines, lineCount);
        Assert.NotNull(footer);
        Assert.Equal(Lines, footer.LineCount);
        Assert.Equal(Format(expectedRunning), footer.ClosingBalance);

        Assert.True(timeToFirstLine < MaxTimeToFirstLine,
            $"First line took {timeToFirstLine?.TotalSeconds:F1} s; expected under {MaxTimeToFirstLine.TotalSeconds} s. Is the statement being buffered or sorted?");
        Assert.True(total.Elapsed < MaxTotalTime,
            $"Statement took {total.Elapsed.TotalSeconds:F1} s; expected under {MaxTotalTime.TotalSeconds} s.");
        Assert.True(growth < MaxLiveMemoryGrowthBytes,
            $"Live memory grew by {Mb(growth)} while streaming; expected under {Mb(MaxLiveMemoryGrowthBytes)}. Is the statement being buffered?");
    }

    /// <summary>
    /// Set-based inserts (seconds, not minutes): <see cref="EarlierMovements"/> movements before the period, then
    /// <see cref="Lines"/> inside it, two per second so that each pair shares an OccurredAt and is ordered by Id.
    /// Lines are inserted in order, so line n gets the n-th Id and the reference VOL-n.
    /// </summary>
    /// <summary>A one-line statement through the same path (the last earlier movement, a minute before the period).</summary>
    private async Task WarmUpAsync()
    {
        using var call = _host.Client.GetStatement(new Proto.GetStatementRequest
        {
            AccountId = _account,
            Currency = "ZAR",
            From = Timestamp.FromDateTimeOffset(From.AddMinutes(-1)),
            To = Timestamp.FromDateTimeOffset(From),
        });

        var lines = 0;
        await foreach (var item in call.ResponseStream.ReadAllAsync())
            if (item.ItemCase == ItemCase.Line)
                lines++;

        Assert.Equal(1, lines);
    }

    private async Task SeedAsync()
    {
        var from = From.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var earlierAmount = EarlierAmount.ToString(CultureInfo.InvariantCulture);

        await _db.ExecuteAsync($"""
            WITH numbers AS (
                SELECT TOP ({EarlierMovements}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO dbo.Movement (AccountId, ExternalRef, Amount, Currency, OccurredAt)
            SELECT N'{_account}', CONCAT(N'EARLIER-', n), {earlierAmount}, 'ZAR',
                   DATEADD(MINUTE, -CAST(n AS INT), CAST('{from}+00:00' AS DATETIMEOFFSET(7)))
            FROM numbers;
            """);

        await _db.ExecuteAsync($"""
            WITH numbers AS (
                SELECT TOP ({Lines}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO dbo.Movement (AccountId, ExternalRef, Amount, Currency, OccurredAt)
            SELECT N'{_account}', CONCAT(N'VOL-', n), CAST((n % 1000) - 499.5 AS DECIMAL(19, 2)), 'ZAR',
                   DATEADD(SECOND, CAST((n - 1) / 2 AS INT), CAST('{from}+00:00' AS DATETIMEOFFSET(7)))
            FROM numbers
            ORDER BY n;
            """);
    }

    /// <summary>Managed heap size after a full, compacting collection: what is actually still referenced.</summary>
    private static long LiveMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetGCMemoryInfo(GCKind.FullBlocking).HeapSizeBytes;
    }

    private static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB";
}
