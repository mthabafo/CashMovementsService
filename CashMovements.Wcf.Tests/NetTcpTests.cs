using System.ServiceModel;
using CashMovements.Wcf.Contracts;
using CashMovements.Wcf.Tests.Support;
using Grpc.Core;

namespace CashMovements.Wcf.Tests;

/// <summary>
/// A real WCF round trip: ServiceHost and ChannelFactory over NetTcp with the gateway's binding (Transport security,
/// Windows credentials, as the current user). The backend is faked; this proves what crosses the wire.
/// </summary>
public sealed class NetTcpTests : IDisposable
{
    private readonly GatewayHost _gateway = new();

    public void Dispose() => _gateway.Dispose();

    [Fact]
    public async Task GetBalance_returns_the_result_over_net_tcp()
    {
        var client = Start(FakeBalanceBackend.Returning("39600.01"));

        var result = await client.GetBalanceAsync("ACC-1001", "ZAR");

        Assert.Equal("ACC-1001", result.AccountId);
        Assert.Equal("ZAR", result.Currency);
        Assert.Equal(39600.01m, result.Amount);
        Assert.Equal(FakeBalanceBackend.AsAt, result.AsAtUtc);
    }

    [Fact]
    public async Task Faults_arrive_typed_over_net_tcp()
    {
        var client = Start(FakeBalanceBackend.Failing(StatusCode.InvalidArgument, "Account ID is required."));

        var fault = await Assert.ThrowsAsync<FaultException<BalanceFault>>(() => client.GetBalanceAsync("", "ZAR"));

        Assert.Equal(BalanceFaultCodes.InvalidArgument, fault.Detail.Code);
        Assert.Equal("Account ID is required.", fault.Detail.Message);
    }

    [Fact]
    public async Task Exception_details_never_cross_the_wire()
    {
        var client = Start(FakeBalanceBackend.Throwing(new InvalidOperationException("Server=prod-sql;Password=secret")));

        var fault = await Assert.ThrowsAsync<FaultException<BalanceFault>>(() => client.GetBalanceAsync("ACC-1", "ZAR"));

        Assert.Equal(BalanceFaultCodes.Internal, fault.Detail.Code);
        Assert.DoesNotContain("secret", fault.Detail.Message);
        Assert.DoesNotContain("secret", fault.Message);
    }

    private ILegacyBalanceService Start(IBalanceBackend backend) => _gateway.Start(backend);
}
