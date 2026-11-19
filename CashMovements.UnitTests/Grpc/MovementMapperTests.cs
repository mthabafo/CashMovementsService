using System.Globalization;
using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;
using CashMovements.Grpc.Mapping;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.UnitTests.Grpc;

public class MovementMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider Clock = new FixedClock(Now);

    private static Proto.RecordMovementRequest Request(string amount = "-250.75", string? narration = null)
    {
        var request = new Proto.RecordMovementRequest
        {
            AccountId = "ACC1",
            ExternalRef = "REF-1",
            Amount = amount,
            Currency = "zar",
            OccurredAt = Timestamp.FromDateTimeOffset(Now.AddDays(-1)),
        };
        if (narration is not null)
            request.Narration = narration;
        return request;
    }

    // ---- Amounts ----

    [Theory]
    [InlineData("-250.75", -250.75)]
    [InlineData("100", 100)]
    [InlineData("+0.5", 0.5)]
    public void Amounts_parse_with_a_dot_regardless_of_server_culture(string input, double expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-ZA"); // uses ',' as decimal separator
            Assert.Equal((decimal)expected, MovementMapper.ParseAmount(input, "amount"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1,50")]
    [InlineData("1,000.00")]
    [InlineData("1e3")]
    [InlineData(" 10")]
    [InlineData("ten")]
    public void Malformed_amounts_are_invalid_argument(string input)
    {
        var ex = Assert.Throws<RpcException>(() => MovementMapper.ParseAmount(input, "amount"));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Theory]
    [InlineData(100, "100.00")]
    [InlineData(-250.5, "-250.50")]
    [InlineData(0, "0.00")]
    public void Amounts_are_formatted_with_two_decimal_places(double amount, string expected)
        => Assert.Equal(expected, MovementMapper.FormatAmount((decimal)amount));

    // ---- RecordMovementRequest → domain ----

    [Fact]
    public void Request_maps_to_a_domain_movement_with_separate_amount_and_currency()
    {
        var movement = MovementMapper.ToDomain(Request(narration: "Rent"), Clock);

        Assert.Equal(-250.75m, movement.Amount);
        Assert.Equal("ZAR", movement.Currency.Code);
        Assert.Equal("Rent", movement.Narration);
        Assert.Equal(Now.AddDays(-1), movement.OccurredAt);
    }

    [Fact]
    public void Unset_narration_maps_to_null()
        => Assert.Null(MovementMapper.ToDomain(Request(), Clock).Narration);

    [Fact]
    public void Missing_occurred_at_is_invalid_argument()
    {
        var request = Request();
        request.OccurredAt = null;

        var ex = Assert.Throws<RpcException>(() => MovementMapper.ToDomain(request, Clock));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public void Business_rule_violations_are_left_to_the_domain()
        => Assert.Throws<InvalidMovementException>(() => MovementMapper.ToDomain(Request(amount: "1.005"), Clock));

    // ---- Domain → proto ----

    [Fact]
    public void Movement_maps_to_proto_with_formatted_amount()
    {
        var movement = Movement.Rehydrate(7, "ACC1", "REF-1", 12.5m, "ZAR", Now.AddDays(-1), null);

        var proto = MovementMapper.ToProto(movement);

        Assert.Equal(7, proto.Id);
        Assert.Equal("12.50", proto.Amount);
        Assert.Equal("ZAR", proto.Currency);
        Assert.False(proto.HasNarration);
        Assert.Equal(Now.AddDays(-1), proto.OccurredAt.ToDateTimeOffset());
    }

    [Fact]
    public void Footer_carries_closing_balance_and_line_count()
    {
        var footer = MovementMapper.ToFooter(-380m, 3);

        Assert.Equal("-380.00", footer.ClosingBalance);
        Assert.Equal(3, footer.LineCount);
    }
}
