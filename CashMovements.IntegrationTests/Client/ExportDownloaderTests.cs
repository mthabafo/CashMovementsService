using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using CashMovements.Grpc.Client;
using CashMovements.IntegrationTests.Support;
using Google.Protobuf.WellKnownTypes;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.IntegrationTests.Client;

/// <summary>
/// The console client's <see cref="ExportDownloader"/>: against the real host it saves and verifies the file and
/// reports progress to 100%; with scripted streams it rejects anything incomplete or corrupt and never leaves a file
/// (or a .partial) behind.
/// </summary>
public sealed class ExportDownloaderTests : IDisposable
{
    private readonly GrpcTestHost _host = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("cashmovements-export-").FullName;

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task Saves_and_verifies_a_real_export_and_reports_progress_to_the_end()
    {
        for (var i = 1; i <= 1500; i++) // about 130 KB: more than one 64 KB chunk
            await _host.Client.RecordMovementAsync(Request($"D-{i}", i));
        var progress = new List<ExportProgress>();

        using var call = _host.Client.ExportStatement(ExportRequest());
        var result = await ExportDownloader.DownloadAsync(ReadExport.Messages(call), null, _folder, progress.Add, default);

        Assert.True(File.Exists(result.Path));
        Assert.Equal(_folder, Path.GetDirectoryName(result.Path));
        Assert.StartsWith("statement_ACC-DOWNLOAD_ZAR_", Path.GetFileName(result.Path));
        Assert.Equal(1500, result.LineCount);
        Assert.Equal(new FileInfo(result.Path).Length, result.ByteCount);
        Assert.Empty(Directory.GetFiles(_folder, "*" + ExportDownloader.PartialSuffix));

        Assert.Equal(0, progress[0].LinesWritten);
        Assert.Equal(1500, progress[^1].LinesWritten);
        Assert.True(progress.Count >= 3, "expected several chunks");
        Assert.All(progress, p => Assert.Equal(1500, p.LineCount));
        Assert.Equal(1.0, progress[^1].Fraction);
    }

    [Fact]
    public async Task A_corrupt_file_is_rejected_and_nothing_is_kept()
    {
        var data = "Type,OccurredAt\r\n"u8.ToArray();
        var messages = Script(
            new ExportMessage.Start("statement.csv", 0),
            new ExportMessage.Chunk(data, 0),
            new ExportMessage.End("0.00", 0, data.Length, new string('0', 64))); // wrong checksum

        var ex = await Assert.ThrowsAsync<ExportIncompleteException>(() => DownloadAsync(messages));

        Assert.Contains("corrupt", ex.Message);
        AssertNoFiles();
    }

    [Fact]
    public async Task An_export_that_stops_before_its_end_message_is_incomplete_and_nothing_is_kept()
    {
        var messages = Script(new ExportMessage.Start("statement.csv", 10), new ExportMessage.Chunk(new byte[100], 4));

        var ex = await Assert.ThrowsAsync<ExportIncompleteException>(() => DownloadAsync(messages));

        Assert.Contains("no end message", ex.Message);
        AssertNoFiles();
    }

    [Fact]
    public async Task Line_counts_that_disagree_are_rejected()
    {
        var data = new byte[10];
        var messages = Script(
            new ExportMessage.Start("statement.csv", 5),
            new ExportMessage.Chunk(data, 4),
            new ExportMessage.End("0.00", 4, data.Length, Sha256(data)));

        await Assert.ThrowsAsync<ExportIncompleteException>(() => DownloadAsync(messages));
        AssertNoFiles();
    }

    [Fact]
    public async Task Cancelling_part_way_deletes_the_partial_file()
    {
        using var cancellation = new CancellationTokenSource();
        var messages = Script(
            new ExportMessage.Start("statement.csv", 10),
            new ExportMessage.Chunk(new byte[100], 5),
            new ExportMessage.Chunk(new byte[100], 10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportDownloader.DownloadAsync(
            messages,
            null,
            _folder,
            p => { if (p.LinesWritten > 0) cancellation.Cancel(); }, // e.g. Ctrl+C after the first chunk
            cancellation.Token));

        AssertNoFiles();
    }

    [Fact]
    public async Task Only_the_file_name_from_the_server_is_used_never_a_path()
    {
        var data = "x"u8.ToArray();
        var messages = Script(
            new ExportMessage.Start(@"..\..\outside.csv", 0),
            new ExportMessage.Chunk(data, 0),
            new ExportMessage.End("0.00", 0, data.Length, Sha256(data)));

        var result = await DownloadAsync(messages);

        Assert.Equal(Path.Combine(_folder, "outside.csv"), result.Path);
    }

    [Fact]
    public async Task Out_can_be_a_file_name()
    {
        var data = "x"u8.ToArray();
        var target = Path.Combine(_folder, "mine.csv");
        var messages = Script(
            new ExportMessage.Start("statement.csv", 0),
            new ExportMessage.Chunk(data, 0),
            new ExportMessage.End("0.00", 0, data.Length, Sha256(data)));

        var result = await ExportDownloader.DownloadAsync(messages, target, "unused", null, default);

        Assert.Equal(target, result.Path);
        Assert.Equal(data, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public void The_progress_line_shows_bar_percentage_counts_size_rate_and_time_left()
    {
        var line = ProgressBar.Format(new ExportProgress(93_000, 150_000, 4_300_000, TimeSpan.FromSeconds(2)));

        Assert.Equal("[############--------]   62%  93,000 / 150,000 lines  4.1 MB  46,500 lines/s  ETA 2s", line);
    }

    [Fact]
    public void The_interactive_bar_redraws_in_place_at_most_ten_times_a_second_and_always_shows_the_end()
    {
        // Interactive is the path a real console takes (output not redirected); the first redraw used to overflow.
        var output = new StringWriter();
        var bar = new ProgressBar(output, interactive: true);

        bar.Report(new ExportProgress(0, 1000, 0, TimeSpan.Zero));                                     // drawn
        bar.Report(new ExportProgress(100, 1000, 9_000, TimeSpan.FromMilliseconds(50)));               // too soon: skipped
        bar.Report(new ExportProgress(500, 1000, 45_000, TimeSpan.FromMilliseconds(150)));             // drawn
        bar.Report(new ExportProgress(1000, 1000, 90_000, TimeSpan.FromMilliseconds(160)));            // finished: always drawn
        bar.Finish();

        var text = output.ToString();
        Assert.Equal(3, text.Split("\r[").Length - 1); // each redraw returns to the start of the line
        Assert.Contains("  50%", text);
        Assert.Contains(" 100%  1,000 / 1,000 lines", text);
        Assert.DoesNotContain("  10%", text);
        Assert.EndsWith(Environment.NewLine, text);
    }

    [Fact]
    public void The_redirected_bar_writes_a_plain_line_every_ten_percent()
    {
        var output = new StringWriter();
        var bar = new ProgressBar(output, interactive: false);

        for (var lines = 0; lines <= 1000; lines += 50)
            bar.Report(new ExportProgress(lines, 1000, lines * 90L, TimeSpan.FromMilliseconds(lines)));

        Assert.Equal(11, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length); // 0%, 10%, ... 100%
    }

    [Theory]
    [InlineData(0, 150_000, 1_000)]            // nothing yet: no rate
    [InlineData(1, 150_000, 100_000_000)]      // one line after a very long time: estimate far beyond a day
    [InlineData(150_000, 150_000, 1)]          // finished
    public void The_time_left_estimate_never_overflows(int linesWritten, int lineCount, int elapsedSeconds)
    {
        var progress = new ExportProgress(linesWritten, lineCount, 0, TimeSpan.FromSeconds(elapsedSeconds));

        var remaining = progress.Remaining;
        var line = ProgressBar.Format(progress);

        Assert.True(remaining is null || remaining.Value <= TimeSpan.FromDays(1));
        Assert.DoesNotContain("ETA", linesWritten == 0 || linesWritten == lineCount ? line : "");
    }

    private Task<ExportResult> DownloadAsync(IAsyncEnumerable<ExportMessage> messages)
        => ExportDownloader.DownloadAsync(messages, null, _folder, null, default);

    private void AssertNoFiles() => Assert.Empty(Directory.GetFiles(_folder));

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static async IAsyncEnumerable<ExportMessage> Script(
        params ExportMessage[] messages)
    {
        foreach (var message in messages)
        {
            await Task.Yield();
            yield return message;
        }
    }

    private static Proto.RecordMovementRequest Request(string externalRef, int n)
        => new()
        {
            AccountId = "ACC-DOWNLOAD",
            ExternalRef = externalRef,
            Amount = "1.25",
            Currency = "ZAR",
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-2).AddSeconds(n)),
            Narration = $"Card payment, store {n % 50}",
        };

    private static Proto.ExportStatementRequest ExportRequest()
        => new()
        {
            AccountId = "ACC-DOWNLOAD",
            Currency = "ZAR",
            From = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-10)),
            To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Format = Proto.ExportFormat.Csv,
        };
}
