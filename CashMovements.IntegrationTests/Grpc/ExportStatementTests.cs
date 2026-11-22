using System.Globalization;
using CashMovements.IntegrationTests.Support;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Proto = CashMovements.Grpc.Contracts;
using ExportCase = CashMovements.Grpc.Contracts.ExportStatementResponse.ItemOneofCase;
using ItemCase = CashMovements.Grpc.Contracts.GetStatementResponse.ItemOneofCase;

namespace CashMovements.IntegrationTests.Grpc;

/// <summary>
/// ExportStatement through the real host (in-memory repository): start, chunks, end; the file says exactly what
/// GetStatement says; the end message's counts and checksum match the bytes; errors behave like GetStatement's.
/// </summary>
public sealed class ExportStatementTests : IDisposable
{
    private const string Account = "ACC-EXPORT-1";

    private readonly GrpcTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private Proto.Movements.MovementsClient Client => _host.Client;

    [Fact]
    public async Task The_exported_file_matches_GetStatement_line_for_line()
    {
        await RecordAsync("E1", "1000", daysAgo: 20);       // before the period: opening balance
        await RecordAsync("E2", "-249.50", daysAgo: 5, "Rent, November");
        await RecordAsync("E3", "35.75", daysAgo: 3, "Refund \"overcharge\"");
        await RecordAsync("E4", "-12", daysAgo: 1);

        var statement = await GetStatementAsync(fromDaysAgo: 10);
        var export = await ExportAsync(fromDaysAgo: 10);

        // Messages: one start, chunks, one end.
        Assert.Equal(ExportCase.Start, export.Order[0]);
        Assert.Equal(ExportCase.End, export.Order[^1]);
        Assert.All(export.Order.Skip(1).SkipLast(1), c => Assert.Equal(ExportCase.Chunk, c));

        // Rows: header, opening, one per statement line, closing.
        var rows = export.Rows;
        var lines = statement.Where(i => i.ItemCase == ItemCase.Line).Select(i => i.Line).ToList();
        var header = statement.Single(i => i.ItemCase == ItemCase.Header).Header;
        var footer = statement.Single(i => i.ItemCase == ItemCase.Footer).Footer;

        Assert.Equal(lines.Count + 3, rows.Count);
        Assert.Equal(["Opening", "", "", "", "", header.OpeningBalance], rows[1].Where((_, i) => i != 1));
        for (var i = 0; i < lines.Count; i++)
        {
            var row = rows[i + 2];
            var line = lines[i];
            Assert.Equal("Movement", row[0]);
            Assert.Equal(line.Movement.OccurredAt.ToDateTimeOffset(), DateTimeOffset.Parse(row[1], CultureInfo.InvariantCulture));
            Assert.Equal(line.Movement.Id.ToString(CultureInfo.InvariantCulture), row[2]);
            Assert.Equal(line.Movement.ExternalRef, row[3]);
            Assert.Equal(line.Movement.Narration ?? "", row[4]);
            Assert.Equal(line.Movement.Amount, row[5]);
            Assert.Equal(line.RunningBalance, row[6]);
        }
        Assert.Equal(["Closing", "", "", "", "", footer.ClosingBalance], rows[^1].Where((_, i) => i != 1));

        // Start and end describe the file exactly.
        Assert.Equal(lines.Count, export.Start!.LineCount);
        Assert.Equal("text/csv", export.Start.ContentType);
        Assert.Equal($"statement_{Account}_ZAR_", export.Start.FileName[..($"statement_{Account}_ZAR_".Length)]);
        Assert.Equal(footer.ClosingBalance, export.End!.ClosingBalance);
        Assert.Equal(lines.Count, export.End.LineCount);
        Assert.Equal(export.File.Length, export.End.ByteCount);
        Assert.Equal(export.Sha256, export.End.Sha256);
        Assert.Equal(lines.Count, export.LinesWritten[^1]);
    }

    [Fact]
    public async Task An_empty_period_exports_just_the_header_opening_and_closing_rows()
    {
        await RecordAsync("E1", "50", daysAgo: 20);

        var export = await ExportAsync(fromDaysAgo: 10);

        Assert.Equal(0, export.Start!.LineCount);
        Assert.Equal(3, export.Rows.Count);
        Assert.Equal("50.00", export.End!.ClosingBalance);
    }

    [Theory]
    [InlineData("RAND", 10, 0)]  // invalid currency
    [InlineData("ZAR", 1, 10)]   // reversed period
    public async Task Invalid_input_is_invalid_argument_before_anything_is_sent(string currency, int fromDaysAgo, int toDaysAgo)
    {
        using var call = Client.ExportStatement(Request(currency, fromDaysAgo, toDaysAgo));

        var ex = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext());

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task An_unknown_format_is_invalid_argument()
    {
        var request = Request("ZAR", 10, 0);
        request.Format = (Proto.ExportFormat)99;
        using var call = Client.ExportStatement(request);

        var ex = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext());

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("not supported", ex.Status.Detail);
    }

    [Fact]
    public async Task A_failure_part_way_ends_the_stream_with_an_error_and_no_end_message()
    {
        await RecordAsync("E1", "10", daysAgo: 3);
        await RecordAsync("E2", "20", daysAgo: 2);
        _host.Repository.FailStreamAfterRows = 1;

        using var call = Client.ExportStatement(Request("ZAR", 10, 0));
        var received = new List<ExportCase>();

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await foreach (var item in call.ResponseStream.ReadAllAsync())
                received.Add(item.ItemCase);
        });

        Assert.Equal(StatusCode.Internal, ex.StatusCode);
        Assert.Equal(ExportCase.Start, received[0]);
        Assert.DoesNotContain(ExportCase.End, received);
        Assert.Equal(0, _host.Repository.OpenSources); // the snapshot was released
    }

    [Fact]
    public async Task A_completed_export_logs_StatementExported_with_the_account_masked()
    {
        await RecordAsync("E1", "10", daysAgo: 3);

        var export = await ExportAsync(fromDaysAgo: 10);

        var entry = Assert.Single(_host.Logs.Entries, e => e.EventId.Id == 2101);
        Assert.Equal("****RT-1", entry.Fields["Account"]);
        Assert.Equal(1, entry.Fields["LineCount"]);
        Assert.Equal(export.File.LongLength, entry.Fields["ByteCount"]);
    }

    private Task<Proto.RecordMovementResponse> RecordAsync(string externalRef, string amount, int daysAgo, string? narration = null)
    {
        var request = new Proto.RecordMovementRequest
        {
            AccountId = Account,
            ExternalRef = externalRef,
            Amount = amount,
            Currency = "ZAR",
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-daysAgo)),
        };
        if (narration is not null)
            request.Narration = narration;

        return Client.RecordMovementAsync(request).ResponseAsync;
    }

    private async Task<List<Proto.GetStatementResponse>> GetStatementAsync(int fromDaysAgo)
    {
        using var call = Client.GetStatement(new Proto.GetStatementRequest
        {
            AccountId = Account,
            Currency = "ZAR",
            From = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.Date.AddDays(-fromDaysAgo)),
            To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.Date.AddDays(1)),
        });

        var items = new List<Proto.GetStatementResponse>();
        await foreach (var item in call.ResponseStream.ReadAllAsync())
            items.Add(item);
        return items;
    }

    private async Task<ReadExport> ExportAsync(int fromDaysAgo)
    {
        using var call = Client.ExportStatement(new Proto.ExportStatementRequest
        {
            AccountId = Account,
            Currency = "ZAR",
            From = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.Date.AddDays(-fromDaysAgo)),
            To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.Date.AddDays(1)),
            Format = Proto.ExportFormat.Csv,
        });

        return await ReadExport.FromAsync(call);
    }

    private static Proto.ExportStatementRequest Request(string currency, int fromDaysAgo, int toDaysAgo)
        => new()
        {
            AccountId = Account,
            Currency = currency,
            From = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-fromDaysAgo)),
            To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-toDaysAgo)),
        };
}
