using System.Globalization;
using System.ServiceModel;
using CashMovements.Wcf.Contracts;
using CashMovements.Wcf.Tests.Support;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.Wcf.Tests.EndToEnd;

/// <summary>
/// The new and legacy entry points give the same outcomes over the same data, with nothing faked:
/// <list type="bullet">
/// <item>new: gRPC client → CashMovements.Grpc → SQL Server</item>
/// <item>legacy: WCF client → net.tcp → gateway → gRPC-Web → the same server → the same database</item>
/// </list>
/// The gRPC side is called over gRPC-Web because .NET Framework can't use HTTP/2 without TLS; that gRPC-Web and HTTP/2
/// give the same results is covered by MovementsGrpcTests.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class ParityTests : IClassFixture<GrpcServerFixture>, IDisposable
{
    private static readonly DateTimeOffset OccurredAt = DateTimeOffset.UtcNow.AddDays(-2);

    private readonly GrpcServerFixture _server;
    private readonly GatewayHost _gateway = new();
    private readonly GrpcBalanceBackend _backend;
    private readonly ILegacyBalanceService _legacy;
    private readonly string _accountA = "PAR-A-" + Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly string _accountB = "PAR-B-" + Guid.NewGuid().ToString("N").Substring(0, 8);

    public ParityTests(GrpcServerFixture server)
    {
        _server = server;
        _backend = new GrpcBalanceBackend(server.GrpcWebAddress, TimeSpan.FromSeconds(10));
        _legacy = _gateway.Start(_backend);
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _backend.Dispose();
    }

    [Fact]
    public async Task Both_return_the_same_balance_for_every_account_and_currency()
    {
        await RecordAsync(_accountA, "A-1", "100.50", "ZAR");
        await RecordAsync(_accountA, "A-2", "-20.25", "ZAR");
        await RecordAsync(_accountA, "A-3", "7", "USD");
        await RecordAsync(_accountB, "B-1", "-5.10", "ZAR");

        await AssertSameBalanceAsync(_accountA, "ZAR", expected: 80.25m);
        await AssertSameBalanceAsync(_accountA, "zar", expected: 80.25m); // input normalised the same way
        await AssertSameBalanceAsync(_accountA, "USD", expected: 7m);
        await AssertSameBalanceAsync(_accountA, "EUR", expected: 0m);      // no movements in that currency
        await AssertSameBalanceAsync(_accountB, "ZAR", expected: -5.10m);  // negative balances allowed
    }

    [Fact]
    public async Task Both_see_a_newly_recorded_movement()
    {
        await RecordAsync(_accountA, "N-1", "10", "ZAR");
        await AssertSameBalanceAsync(_accountA, "ZAR", expected: 10m);

        await RecordAsync(_accountA, "N-2", "2.50", "ZAR");
        await AssertSameBalanceAsync(_accountA, "ZAR", expected: 12.50m);
    }

    [Fact]
    public async Task A_retried_submission_is_counted_once_by_both()
    {
        await RecordAsync(_accountA, "R-1", "40", "ZAR");
        await RecordAsync(_accountA, "R-1", "40.00", "ZAR"); // a retry, not a second movement

        await AssertSameBalanceAsync(_accountA, "ZAR", expected: 40m);
    }

    [Theory]
    [InlineData("", "ZAR")]
    [InlineData("ACC-1", "RAND")]
    [InlineData("ACC-1", "")]
    [InlineData("   ", "ZAR")]
    [InlineData("ACC-12345678901234567890123456789012", "ZAR")] // 35 characters, one over the limit
    public async Task Invalid_input_is_rejected_with_the_same_message_by_both(string accountId, string currency)
    {
        var grpc = await Assert.ThrowsAsync<RpcException>(async () =>
            await _server.Client.GetBalanceAsync(new Proto.GetBalanceRequest { AccountId = accountId, Currency = currency }));
        var legacy = await Assert.ThrowsAsync<FaultException<BalanceFault>>(() => _legacy.GetBalanceAsync(accountId, currency));

        Assert.Equal(StatusCode.InvalidArgument, grpc.StatusCode);
        Assert.Equal(BalanceFaultCodes.InvalidArgument, legacy.Detail.Code);
        Assert.Equal(grpc.Status.Detail, legacy.Detail.Message);
    }

    [Fact]
    public async Task A_legacy_call_can_be_followed_into_the_server_logs_by_its_correlation_id()
    {
        await _legacy.GetBalanceAsync(_accountA, "ZAR");
        var correlationId = (string)_gateway.Logger.Entries.Last().Scope["CorrelationId"]!;

        // The server writes JSON log lines; its call summary (event 2001, GrpcCallCompleted) for this request carries the
        // gateway's correlation ID. The JSON formatter writes event IDs as numbers, not names.
        var summary = await _server.WaitForOutputLineAsync(line => line.Contains(correlationId) && line.Contains("\"EventId\":2001"));

        Assert.True(summary is not null, $"No GrpcCallCompleted line with correlation ID {correlationId}. Server output:{Environment.NewLine}{_server.OutputTail()}");
        Assert.Contains("GetBalance", summary);
        Assert.Contains("\"StatusCode\":\"OK\"", summary);
        Assert.DoesNotContain(_accountA, summary);
    }

    private async Task AssertSameBalanceAsync(string accountId, string currency, decimal expected)
    {
        var grpc = await _server.Client.GetBalanceAsync(new Proto.GetBalanceRequest { AccountId = accountId, Currency = currency });
        var legacy = await _legacy.GetBalanceAsync(accountId, currency);

        // The same answer from both...
        Assert.Equal(decimal.Parse(grpc.Amount, CultureInfo.InvariantCulture), legacy.Amount);
        Assert.Equal(grpc.AccountId, legacy.AccountId);
        Assert.Equal(grpc.Currency, legacy.Currency);
        Assert.Equal(DateTimeKind.Utc, legacy.AsAtUtc.Kind);
        Assert.True((legacy.AsAtUtc - grpc.AsAt.ToDateTime()).Duration() < TimeSpan.FromSeconds(30));

        // ...and the right one, so they can't both be wrong in the same way.
        Assert.Equal(expected, legacy.Amount);
        Assert.Equal(currency.ToUpperInvariant(), legacy.Currency);
    }

    private Task<Proto.RecordMovementResponse> RecordAsync(string accountId, string externalRef, string amount, string currency)
        => _server.Client.RecordMovementAsync(new Proto.RecordMovementRequest
        {
            AccountId = accountId,
            ExternalRef = externalRef,
            Amount = amount,
            Currency = currency,
            OccurredAt = Timestamp.FromDateTimeOffset(OccurredAt),
        }).ResponseAsync;
}
