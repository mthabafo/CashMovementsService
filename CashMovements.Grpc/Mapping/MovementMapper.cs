using System.Globalization;
using CashMovements.Domain.ReadModels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using DomainMovement = CashMovements.Domain.Entities.Movement;
using DomainStatementHeader = CashMovements.Domain.ReadModels.StatementHeader;
using DomainStatementLine = CashMovements.Domain.ReadModels.StatementLine;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.Grpc.Mapping;

/// <summary>
/// Translates between protobuf contracts and the domain. Owns the wire formats: amounts as invariant-culture
/// decimal strings (protobuf has no decimal type and double would lose cents) and timestamps as UTC.
/// Malformed wire values become INVALID_ARGUMENT; business rules are left to the domain.
/// </summary>
public static class MovementMapper
{
    private const string AmountFormat = "0.00";

    // ---------- Requests → domain ----------

    public static DomainMovement ToDomain(Proto.RecordMovementRequest request, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(request);

        return DomainMovement.Create(
            request.AccountId,
            request.ExternalRef,
            ParseAmount(request.Amount, "amount"),
            request.Currency,
            ToDateTimeOffset(request.OccurredAt, "occurred_at"),
            request.HasNarration ? request.Narration : null,
            clock);
    }

    /// <summary>
    /// Parses a decimal string independent of the server's culture: "-250.75" works on an en-ZA server too.
    /// Thousands separators, exponents and whitespace are rejected.
    /// </summary>
    public static decimal ParseAmount(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw InvalidArgument($"{fieldName} is required.");

        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount))
            throw InvalidArgument($"{fieldName} '{value}' is not a valid decimal amount (use '.' as the decimal separator).");

        return amount;
    }

    public static DateTimeOffset ToDateTimeOffset(Timestamp? timestamp, string fieldName)
    {
        if (timestamp is null)
            throw InvalidArgument($"{fieldName} is required.");

        return timestamp.ToDateTimeOffset();
    }

    // ---------- Domain → responses ----------

    public static string FormatAmount(decimal amount)
        => amount.ToString(AmountFormat, CultureInfo.InvariantCulture);

    public static Proto.Movement ToProto(DomainMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);

        var proto = new Proto.Movement
        {
            Id = movement.Id,
            AccountId = movement.AccountId.Value,
            ExternalRef = movement.ExternalRef.Value,
            Amount = FormatAmount(movement.Amount),
            Currency = movement.Currency.Code,
            OccurredAt = Timestamp.FromDateTimeOffset(movement.OccurredAt),
            RecordedAt = Timestamp.FromDateTimeOffset(movement.RecordedAt),
        };

        if (movement.Narration is not null)
            proto.Narration = movement.Narration;

        return proto;
    }

    public static Proto.GetBalanceResponse ToProto(Balance balance)
    {
        ArgumentNullException.ThrowIfNull(balance);

        return new Proto.GetBalanceResponse
        {
            AccountId = balance.AccountId.Value,
            Currency = balance.Currency.Code,
            Amount = FormatAmount(balance.Amount),
            AsAt = Timestamp.FromDateTimeOffset(balance.AsAt),
        };
    }

    public static Proto.StatementHeader ToProto(DomainStatementHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        return new Proto.StatementHeader
        {
            AccountId = header.AccountId.Value,
            Currency = header.Currency.Code,
            From = Timestamp.FromDateTimeOffset(header.From),
            To = Timestamp.FromDateTimeOffset(header.To),
            OpeningBalance = FormatAmount(header.OpeningBalance),
        };
    }

    public static Proto.StatementLine ToProto(DomainStatementLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new Proto.StatementLine
        {
            Movement = ToProto(line.Movement),
            RunningBalance = FormatAmount(line.RunningBalance),
        };
    }

    public static Proto.StatementFooter ToFooter(decimal closingBalance, int lineCount)
        => new()
        {
            ClosingBalance = FormatAmount(closingBalance),
            LineCount = lineCount,
        };

    private static RpcException InvalidArgument(string message)
        => new(new Status(StatusCode.InvalidArgument, message));
}
