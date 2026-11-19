using CashMovements.Application.Interfaces;
using CashMovements.Grpc.Mapping;
using Grpc.Core;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.Grpc.Services;

/// <summary>
/// gRPC endpoint for cash movements. Deliberately thin: map the request, call <see cref="IMovementService"/>,
/// map the response. Validation lives in the domain; exceptions become status codes in the interceptor.
/// </summary>
public sealed class MovementGrpcService : Proto.Movements.MovementsBase
{
    private readonly IMovementService _movements;
    private readonly TimeProvider _clock;

    public MovementGrpcService(IMovementService movements, TimeProvider clock)
    {
        _movements = movements;
        _clock = clock;
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
}
