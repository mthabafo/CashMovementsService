using System.Diagnostics;
using System.Security.Cryptography;

namespace CashMovements.Grpc.Client;

/// <summary>One message of a statement export, independent of the generated gRPC types (so it can be tested on its own).</summary>
public abstract record ExportMessage
{
    private ExportMessage()
    {
    }

    public sealed record Start(string FileName, int LineCount) : ExportMessage;

    public sealed record Chunk(ReadOnlyMemory<byte> Data, int LinesWritten) : ExportMessage;

    public sealed record End(string ClosingBalance, int LineCount, long ByteCount, string Sha256) : ExportMessage;
}

/// <summary>How far an export has got. <see cref="LineCount"/> is the total announced by the server.</summary>
public sealed record ExportProgress(int LinesWritten, int LineCount, long BytesWritten, TimeSpan Elapsed)
{
    public double Fraction => LineCount == 0 ? 1 : Math.Clamp((double)LinesWritten / LineCount, 0, 1);

    public double LinesPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : LinesWritten / Elapsed.TotalSeconds;

    /// <summary>Estimated time left at the current rate; null until there's a rate, or when it's too far off to be useful.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (LinesPerSecond <= 0)
                return null;

            var seconds = Math.Max(0, LineCount - LinesWritten) / LinesPerSecond;
            return seconds < MaxEstimate.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
        }
    }

    private static readonly TimeSpan MaxEstimate = TimeSpan.FromDays(1);
}

public sealed record ExportResult(string Path, int LineCount, long ByteCount, string ClosingBalance, TimeSpan Elapsed);

/// <summary>The export didn't arrive complete and intact; no file was kept.</summary>
public sealed class ExportIncompleteException(string message) : Exception(message);

/// <summary>
/// Saves a streamed statement export to a file. Bytes go to <c>&lt;file&gt;.partial</c> as they arrive; only when the
/// server's end message matches what was received (SHA-256, byte count, line count) is it renamed to the real name.
/// On any failure or cancellation the partial file is deleted, so a file with the final name is always complete.
/// </summary>
public static class ExportDownloader
{
    public const string PartialSuffix = ".partial";

    /// <summary>The folder exports are saved to by default, at the repository root.</summary>
    public const string ExportsFolderName = "exports";

    private const string SolutionFile = "CashMovements.slnx";

    /// <summary>
    /// The default exports folder: <c>exports</c> at the repository root (the folder holding CashMovements.slnx), found
    /// by walking up from <paramref name="start"/>, so it's the same however the client is launched (Visual Studio,
    /// test.ps1, dotnet run). Without a repository above <paramref name="start"/>, <c>exports</c> under
    /// <paramref name="fallback"/>. The folder is created by <see cref="DownloadAsync"/> when needed.
    /// </summary>
    public static string DefaultFolder(string start, string fallback)
    {
        for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, SolutionFile)))
                return Path.Combine(folder.FullName, ExportsFolderName);
        }

        return Path.GetFullPath(Path.Combine(fallback, ExportsFolderName));
    }

    /// <param name="messages">The export: one start, chunks, one end.</param>
    /// <param name="outputPath">File or folder to save to; null means the server's file name in <paramref name="defaultFolder"/>.</param>
    /// <param name="defaultFolder">Where to save when <paramref name="outputPath"/> is null.</param>
    /// <param name="onProgress">Called after every chunk (and once at the start).</param>
    /// <exception cref="ExportIncompleteException">The export ended early or doesn't match its end message.</exception>
    public static async Task<ExportResult> DownloadAsync(
        IAsyncEnumerable<ExportMessage> messages,
        string? outputPath,
        string defaultFolder,
        Action<ExportProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var reader = messages.GetAsyncEnumerator(cancellationToken);

        if (!await reader.MoveNextAsync() || reader.Current is not ExportMessage.Start start)
            throw new ExportIncompleteException("The export didn't begin with a start message.");

        var path = TargetPath(outputPath, defaultFolder, start.FileName);
        var partial = path + PartialSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var kept = false;

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytes = 0;
            var linesWritten = 0;
            ExportMessage.End? end = null;

            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                onProgress?.Invoke(new ExportProgress(0, start.LineCount, 0, stopwatch.Elapsed));

                while (await reader.MoveNextAsync())
                {
                    switch (reader.Current)
                    {
                        case ExportMessage.Chunk chunk when end is null:
                            await file.WriteAsync(chunk.Data, cancellationToken);
                            hash.AppendData(chunk.Data.Span);
                            bytes += chunk.Data.Length;
                            linesWritten = chunk.LinesWritten;
                            onProgress?.Invoke(new ExportProgress(linesWritten, start.LineCount, bytes, stopwatch.Elapsed));
                            break;

                        case ExportMessage.End last when end is null:
                            end = last;
                            break;

                        default:
                            throw new ExportIncompleteException($"Unexpected {reader.Current.GetType().Name} message in the export.");
                    }
                }
            }

            if (end is null)
                throw new ExportIncompleteException("The export ended before it was complete (no end message).");

            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(sha256, end.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ExportIncompleteException($"The file is corrupt: its SHA-256 is {sha256}, the server's is {end.Sha256}.");
            if (bytes != end.ByteCount)
                throw new ExportIncompleteException($"The file is incomplete: received {bytes:N0} bytes, the server sent {end.ByteCount:N0}.");
            if (end.LineCount != start.LineCount || linesWritten != end.LineCount)
                throw new ExportIncompleteException($"The line counts don't match: announced {start.LineCount:N0}, written {linesWritten:N0}, ended with {end.LineCount:N0}.");

            File.Move(partial, path, overwrite: true);
            kept = true;
            return new ExportResult(path, end.LineCount, bytes, end.ClosingBalance, stopwatch.Elapsed);
        }
        finally
        {
            if (!kept)
                TryDelete(partial);
        }
    }

    private static string TargetPath(string? outputPath, string defaultFolder, string fileName)
    {
        // Only the file name from the server is used, never a path it might contain.
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "statement.csv";

        if (string.IsNullOrWhiteSpace(outputPath))
            return Path.GetFullPath(Path.Combine(defaultFolder, safeName));

        return Directory.Exists(outputPath)
            ? Path.GetFullPath(Path.Combine(outputPath, safeName))
            : Path.GetFullPath(outputPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
