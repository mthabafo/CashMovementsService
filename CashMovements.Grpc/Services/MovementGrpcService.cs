using System.Diagnostics;
using CashMovements.Application.Interfaces;
using CashMovements.Application.Logging;
using CashMovements.Grpc.Export;
using CashMovements.Grpc.Mapping;
using Google.Protobuf;
using Grpc.Core;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.Grpc.Services;

/// <summary>
/// gRPC endpoint for cash movements. Deliberately thin: map the request, call <see cref="IMovementService"/>,
/// map the response. Validation lives in the domain; exceptions become status codes in the interceptor.
/// </summary>
public sealed partial class MovementGrpcService : Proto.Movements.MovementsBase
{
    private readonly IMovementService _movements;
    private readonly TimeProvider _clock;
    private readonly ILogger<MovementGrpcService> _logger;

    public MovementGrpcService(IMovementService movements, TimeProvider clock, ILogger<MovementGrpcService> logger)
    {
        _movements = movements;
        _clock = clock;
        _logger = logger;
    }

    public override async Task<Proto.GetBalanceResponse> GetBalance(
        Proto.GetBalanceRequest request,
        ServerCallContext context)
    {
        var balance = await _movements.GetBalanceAsync(
            request.AccountId,
            request.Currency,
            context.CancellationToken);

        return MovementMapper.ToProto(balance);
    }

    public override async Task GetStatement(
        Proto.GetStatementRequest request,
        IServerStreamWriter<Proto.GetStatementResponse> responseStream,
        ServerCallContext context)
    {
        var cancellationToken = context.CancellationToken;

        // Holds the database snapshot open until the last line is written (or the call fails / is cancelled).
        await using var statement = await _movements.GetStatementAsync(
            request.AccountId,
            request.Currency,
            MovementMapper.ToDateTimeOffset(request.From, "from"),
            MovementMapper.ToDateTimeOffset(request.To, "to"),
            cancellationToken);

        // 1. Header: everything known before the first line.
        await responseStream.WriteAsync(new Proto.GetStatementResponse
        {
            Header = MovementMapper.ToProto(statement.Header),
        });

        // 2. Lines, as they are read from the database.
        var closingBalance = statement.Header.OpeningBalance;
        var lineCount = 0;

        await foreach (var line in statement.Lines.WithCancellation(cancellationToken))
        {
            await responseStream.WriteAsync(new Proto.GetStatementResponse
            {
                Line = MovementMapper.ToProto(line),
            });

            closingBalance = line.RunningBalance;
            lineCount++;
        }

        // 3. Footer: closing balance and count, which also marks the statement as complete.
        await responseStream.WriteAsync(new Proto.GetStatementResponse
        {
            Footer = MovementMapper.ToFooter(closingBalance, lineCount),
        });
    }

    public override async Task<Proto.RecordMovementResponse> RecordMovement(
        Proto.RecordMovementRequest request,
        ServerCallContext context)
    {
        var movement = MovementMapper.ToDomain(request, _clock);

        var recorded = await _movements.RecordMovementAsync(movement, context.CancellationToken);

        return new Proto.RecordMovementResponse
        {
            Movement = MovementMapper.ToProto(recorded),
        };
    }

    public override async Task ExportStatement(
        Proto.ExportStatementRequest request,
        IServerStreamWriter<Proto.ExportStatementResponse> responseStream,
        ServerCallContext context)
    {
        if (request.Format is not (Proto.ExportFormat.Unspecified or Proto.ExportFormat.Csv))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Export format {request.Format} is not supported."));

        var cancellationToken = context.CancellationToken;
        var started = Stopwatch.GetTimestamp();

        // The same statement as GetStatement (validation, snapshot, running balances), written out as CSV.
        await using var statement = await _movements.GetStatementAsync(
            request.AccountId,
            request.Currency,
            MovementMapper.ToDateTimeOffset(request.From, "from"),
            MovementMapper.ToDateTimeOffset(request.To, "to"),
            cancellationToken);

        // 1. Start: the line count comes from the statement's own snapshot, so it matches what follows exactly.
        var lineCount = await statement.CountLinesAsync(cancellationToken);
        await responseStream.WriteAsync(new Proto.ExportStatementResponse
        {
            Start = new Proto.ExportStart
            {
                FileName = StatementCsvExport.FileName(statement.Header),
                ContentType = StatementCsvExport.ContentType,
                LineCount = lineCount,
            },
        });

        // 2. Chunks, as the lines are read: one buffer at a time, however large the statement.
        var export = new StatementCsvExport(statement.Header, statement.Lines);
        await foreach (var (data, linesWritten) in export.ChunksAsync(cancellationToken))
        {
            await responseStream.WriteAsync(new Proto.ExportStatementResponse
            {
                Chunk = new Proto.ExportChunk { Data = ByteString.CopyFrom(data.Span), LinesWritten = linesWritten },
            });
        }

        // 3. End: what the client checks the file against.
        await responseStream.WriteAsync(new Proto.ExportStatementResponse
        {
            End = new Proto.ExportEnd
            {
                ClosingBalance = MovementMapper.FormatAmount(export.ClosingBalance),
                LineCount = export.LineCount,
                ByteCount = export.ByteCount,
                Sha256 = export.Sha256,
            },
        });

        StatementExported(
            _logger,
            LogMask.Account(statement.Header.AccountId.Value),
            statement.Header.Currency.Code,
            export.LineCount,
            export.ByteCount,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    [LoggerMessage(EventId = 2101, EventName = "StatementExported", Level = LogLevel.Information,
        Message = "Statement for account {Account} in {Currency} exported as CSV: {LineCount} lines, {ByteCount} bytes in {ElapsedMs:0} ms.")]
    private static partial void StatementExported(ILogger logger, string account, string currency, int lineCount, long byteCount, double elapsedMs);
}
