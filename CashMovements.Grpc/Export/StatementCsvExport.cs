using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CashMovements.Domain.ReadModels;

namespace CashMovements.Grpc.Export;

/// <summary>
/// Turns a streamed statement into a CSV file, produced in chunks as the lines arrive, so memory stays flat however
/// large the statement is. After <see cref="ChunksAsync"/> completes, <see cref="ClosingBalance"/>,
/// <see cref="LineCount"/>, <see cref="ByteCount"/> and <see cref="Sha256"/> describe the whole file.
/// <para>
/// Format: RFC 4180 CSV, UTF-8 with a byte-order mark (so Excel reads accented narrations correctly), CRLF line endings.
/// Columns: <c>Type, OccurredAt, MovementId, ExternalRef, Narration, Amount, RunningBalance</c>; one <c>Opening</c> row,
/// one <c>Movement</c> row per statement line, one <c>Closing</c> row, so the file stands on its own.
/// </para>
/// </summary>
public sealed class StatementCsvExport
{
    public const string ContentType = "text/csv";
    public const int DefaultChunkSize = 64 * 1024;

    private const string HeaderRow = "Type,OccurredAt,MovementId,ExternalRef,Narration,Amount,RunningBalance";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The UTF-8 byte-order mark (EF BB BF) that starts the file.</summary>
    public static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private readonly StatementHeader _header;
    private readonly IAsyncEnumerable<StatementLine> _lines;
    private readonly int _chunkSize;
    private string? _sha256;

    public StatementCsvExport(StatementHeader header, IAsyncEnumerable<StatementLine> lines, int chunkSize = DefaultChunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        _header = header ?? throw new ArgumentNullException(nameof(header));
        _lines = lines ?? throw new ArgumentNullException(nameof(lines));
        _chunkSize = chunkSize;
        ClosingBalance = header.OpeningBalance;
    }

    public decimal ClosingBalance { get; private set; }

    /// <summary>Statement lines written (Opening and Closing rows not included).</summary>
    public int LineCount { get; private set; }

    public long ByteCount { get; private set; }

    /// <summary>Lowercase hex SHA-256 of every byte produced. Available once <see cref="ChunksAsync"/> has completed.</summary>
    public string Sha256 => _sha256 ?? throw new InvalidOperationException("The export hasn't finished yet.");

    /// <summary>A file name safe for any file system, e.g. <c>statement_ACC-1001_ZAR_20261001_20261101.csv</c>.</summary>
    public static string FileName(StatementHeader header)
    {
        var account = new string(header.AccountId.Value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return $"statement_{account}_{header.Currency.Code}_{header.From.UtcDateTime:yyyyMMdd}_{header.To.UtcDateTime:yyyyMMdd}.csv";
    }

    /// <summary>The file's bytes, about <c>chunkSize</c> at a time, each with the number of statement lines written so far.</summary>
    public async IAsyncEnumerable<(ReadOnlyMemory<byte> Data, int LinesWritten)> ChunksAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var text = new StringBuilder(_chunkSize + 1024);
        var first = true;

        (ReadOnlyMemory<byte> Data, int LinesWritten) Flush()
        {
            var body = Utf8.GetBytes(text.ToString());
            text.Clear();

            var bytes = first ? [.. ByteOrderMark, .. body] : body; // the byte-order mark starts the file
            first = false;

            hash.AppendData(bytes);
            ByteCount += bytes.Length;
            return (bytes, LineCount);
        }

        AppendRow(text, HeaderRow);
        AppendRow(text, Row("Opening", Timestamp(_header.From), "", "", "", "", Amount(_header.OpeningBalance)));

        await foreach (var line in _lines.WithCancellation(cancellationToken))
        {
            var movement = line.Movement;
            AppendRow(text, Row(
                "Movement",
                Timestamp(movement.OccurredAt),
                movement.Id.ToString(CultureInfo.InvariantCulture),
                Text(movement.ExternalRef.Value),
                Text(movement.Narration),
                Amount(movement.Amount),
                Amount(line.RunningBalance)));

            ClosingBalance = line.RunningBalance;
            LineCount++;

            if (text.Length >= _chunkSize)
                yield return Flush();
        }

        AppendRow(text, Row("Closing", Timestamp(_header.To), "", "", "", "", Amount(ClosingBalance)));
        yield return Flush();

        _sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendRow(StringBuilder text, string row) => text.Append(row).Append("\r\n");

    private static string Row(params string[] fields) => string.Join(",", fields.Select(Escape));

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Free text from callers. A value starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or carriage return is
    /// prefixed with <c>'</c>, so a spreadsheet shows it as text instead of running it as a formula (CSV injection).
    /// </summary>
    private static string Text(string? value)
        => string.IsNullOrEmpty(value) || value[0] is not ('=' or '+' or '-' or '@' or '\t' or '\r') ? value ?? "" : "'" + value;

    /// <summary>RFC 4180: a field containing a comma, quote or line break is quoted, with quotes doubled.</summary>
    private static string Escape(string field)
        => field.IndexOfAny([',', '"', '\r', '\n']) < 0 ? field : "\"" + field.Replace("\"", "\"\"") + "\"";
}
