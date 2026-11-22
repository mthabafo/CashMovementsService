using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CashMovements.Grpc.Client;
using Grpc.Core;
using Proto = CashMovements.Grpc.Contracts;
using ExportCase = CashMovements.Grpc.Contracts.ExportStatementResponse.ItemOneofCase;

namespace CashMovements.IntegrationTests.Support;

/// <summary>Reads an ExportStatement stream: the start, the reassembled file, the end, and the order the messages came in.</summary>
internal sealed record ReadExport(
    Proto.ExportStart? Start,
    byte[] File,
    Proto.ExportEnd? End,
    IReadOnlyList<ExportCase> Order,
    IReadOnlyList<int> LinesWritten)
{
    public string Sha256 => Convert.ToHexString(SHA256.HashData(File)).ToLowerInvariant();

    /// <summary>The CSV rows (after the byte-order mark), each split into fields.</summary>
    public IReadOnlyList<string[]> Rows => Csv.Parse(Encoding.UTF8.GetString(File, 3, File.Length - 3));

    public static async Task<ReadExport> FromAsync(AsyncServerStreamingCall<Proto.ExportStatementResponse> call)
    {
        Proto.ExportStart? start = null;
        Proto.ExportEnd? end = null;
        var file = new MemoryStream();
        var order = new List<ExportCase>();
        var linesWritten = new List<int>();

        await foreach (var item in call.ResponseStream.ReadAllAsync())
        {
            order.Add(item.ItemCase);
            switch (item.ItemCase)
            {
                case ExportCase.Start:
                    start = item.Start;
                    break;
                case ExportCase.Chunk:
                    item.Chunk.Data.WriteTo(file);
                    linesWritten.Add(item.Chunk.LinesWritten);
                    break;
                case ExportCase.End:
                    end = item.End;
                    break;
            }
        }

        return new ReadExport(start, file.ToArray(), end, order, linesWritten);
    }

    /// <summary>The same mapping the client's export command uses, from gRPC messages to <see cref="ExportMessage"/>s.</summary>
    public static async IAsyncEnumerable<ExportMessage> Messages(
        AsyncServerStreamingCall<Proto.ExportStatementResponse> call,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            yield return item.ItemCase switch
            {
                ExportCase.Start => new ExportMessage.Start(item.Start.FileName, item.Start.LineCount),
                ExportCase.Chunk => new ExportMessage.Chunk(item.Chunk.Data.Memory, item.Chunk.LinesWritten),
                ExportCase.End => new ExportMessage.End(item.End.ClosingBalance, item.End.LineCount, item.End.ByteCount, item.End.Sha256),
                _ => throw new InvalidOperationException(item.ItemCase.ToString()),
            };
        }
    }
}

/// <summary>A minimal RFC 4180 reader for checking exported files: quoted fields, doubled quotes, CRLF rows.</summary>
internal static class Csv
{
    public static IReadOnlyList<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                rows.Add([.. fields]);
                fields.Clear();
                i++;
            }
            else field.Append(c);
        }

        return rows;
    }
}
