using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CashMovements.Domain.Entities;
using CashMovements.Domain.ReadModels;
using CashMovements.Domain.ValueObjects;
using CashMovements.Grpc.Export;

namespace CashMovements.UnitTests.Grpc;

public class StatementCsvExportTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static StatementHeader Header(decimal opening = 100m, string account = "ACC-1001")
        => StatementHeader.Create(AccountId.From(account), Currency.From("ZAR"), From, To, opening);

    private static StatementLine Line(long id, decimal amount, decimal running, string? narration = null, string externalRef = "REF", int day = 2)
        => new(Movement.Rehydrate(id, "ACC-1001", externalRef + id, amount, "ZAR", From.AddDays(day).AddHours(9), narration), running);

    private static async IAsyncEnumerable<StatementLine> Lines(params StatementLine[] lines)
    {
        foreach (var line in lines)
        {
            await Task.Yield();
            yield return line;
        }
    }

    private static async Task<(byte[] Bytes, List<int> LinesWritten)> RunAsync(StatementCsvExport export)
    {
        var bytes = new List<byte>();
        var progress = new List<int>();
        await foreach (var (data, linesWritten) in export.ChunksAsync())
        {
            bytes.AddRange(data.ToArray());
            progress.Add(linesWritten);
        }

        return (bytes.ToArray(), progress);
    }

    private static string[] Rows(byte[] bytes) => Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n")[..^1];

    [Fact]
    public async Task The_file_starts_with_a_UTF8_byte_order_mark_and_the_header_row()
    {
        var (bytes, _) = await RunAsync(new StatementCsvExport(Header(), Lines()));

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("Type,OccurredAt,MovementId,ExternalRef,Narration,Amount,RunningBalance", Rows(bytes)[0]);
    }

    [Fact]
    public async Task Writes_an_opening_row_one_row_per_line_and_a_closing_row()
    {
        var export = new StatementCsvExport(Header(opening: 100m), Lines(
            Line(1, 250.50m, 350.50m, "Salary"),
            Line(2, -400m, -49.50m, null, day: 3)));

        var (bytes, _) = await RunAsync(export);

        Assert.Equal(
            [
                "Type,OccurredAt,MovementId,ExternalRef,Narration,Amount,RunningBalance",
                "Opening,2026-10-01T00:00:00.0000000Z,,,,,100.00",
                "Movement,2026-10-03T09:00:00.0000000Z,1,REF1,Salary,250.50,350.50",
                "Movement,2026-10-04T09:00:00.0000000Z,2,REF2,,-400.00,-49.50",
                "Closing,2026-11-01T00:00:00.0000000Z,,,,,-49.50",
            ],
            Rows(bytes));
        Assert.Equal(-49.50m, export.ClosingBalance);
        Assert.Equal(2, export.LineCount);
    }

    [Fact]
    public async Task An_empty_statement_has_the_opening_balance_as_its_closing_balance()
    {
        var export = new StatementCsvExport(Header(opening: 75m), Lines());

        var (bytes, progress) = await RunAsync(export);

        Assert.Equal(3, Rows(bytes).Length);
        Assert.Equal("Closing,2026-11-01T00:00:00.0000000Z,,,,,75.00", Rows(bytes)[2]);
        Assert.Equal(0, export.LineCount);
        Assert.Equal([0], progress);
    }

    [Theory]
    [InlineData("Card payment, store 7", "\"Card payment, store 7\"")]
    [InlineData("Coffee at \"The Bean\"", "\"Coffee at \"\"The Bean\"\"\"")]
    [InlineData("Line one\nline two", "\"Line one\nline two\"")]
    public async Task Text_with_commas_quotes_or_line_breaks_is_quoted(string narration, string expected)
    {
        var (bytes, _) = await RunAsync(new StatementCsvExport(Header(), Lines(Line(1, 1m, 101m, narration))));

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains($",REF1,{expected},1.00,101.00\r\n", text);
    }

    [Theory]
    [InlineData("=SUM(A1:A9)")]
    [InlineData("+27 82 000 0000")]
    [InlineData("-refund")]
    [InlineData("@cmd")]
    public async Task Text_a_spreadsheet_would_run_as_a_formula_is_prefixed_with_a_quote(string narration)
    {
        var (bytes, _) = await RunAsync(new StatementCsvExport(Header(), Lines(Line(1, 1m, 101m, narration))));

        Assert.Contains($",REF1,'{narration},", Rows(bytes)[2]);
    }

    [Fact]
    public async Task Amounts_and_dates_do_not_depend_on_the_machine_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // ',' as the decimal separator

            var (bytes, _) = await RunAsync(new StatementCsvExport(Header(opening: 1234.5m), Lines(Line(1, -0.25m, 1234.25m))));

            Assert.Equal("Movement,2026-10-03T09:00:00.0000000Z,1,REF1,,-0.25,1234.25", Rows(bytes)[2]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Chunks_add_up_to_the_same_file_with_matching_counts_and_checksum()
    {
        var lines = Enumerable.Range(1, 500).Select(i => Line(i, 1m, 100m + i, $"Movement {i}")).ToArray();
        var whole = new StatementCsvExport(Header(), Lines(lines));
        var chunked = new StatementCsvExport(Header(), Lines(lines), chunkSize: 1000);

        var (wholeBytes, wholeProgress) = await RunAsync(whole);
        var (chunkedBytes, chunkedProgress) = await RunAsync(chunked);

        Assert.Single(wholeProgress);
        Assert.True(chunkedProgress.Count > 10);
        Assert.Equal(wholeBytes, chunkedBytes);
        Assert.Equal(chunkedProgress.OrderBy(n => n), chunkedProgress); // progress only goes up...
        Assert.Equal(500, chunkedProgress[^1]);                        // ...and ends at the line count
        Assert.Equal(chunkedBytes.Length, chunked.ByteCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(chunkedBytes)).ToLowerInvariant(), chunked.Sha256);
        Assert.Equal(whole.Sha256, chunked.Sha256);
    }

    [Fact]
    public void The_checksum_is_not_available_before_the_export_finishes()
        => Assert.Throws<InvalidOperationException>(() => new StatementCsvExport(Header(), Lines()).Sha256);

    [Theory]
    [InlineData("ACC-1001", "statement_ACC-1001_ZAR_20261001_20261101.csv")]
    [InlineData("ACC/../1 2", "statement_ACC____1_2_ZAR_20261001_20261101.csv")]
    public void The_file_name_is_safe_for_file_systems(string account, string expected)
        => Assert.Equal(expected, StatementCsvExport.FileName(Header(account: account)));
}
