using System.Net;
using System.Net.Sockets;
using System.ServiceModel;
using CashMovements.Wcf.Contracts;
using Grpc.Core;

namespace CashMovements.Wcf.Tests;

/// <summary>
/// A real WCF round trip: ServiceHost and ChannelFactory over NetTcp with the gateway's binding (Transport security,
/// Windows credentials, as the current user). The backend is faked; this proves what crosses the wire.
/// </summary>
public sealed class NetTcpTests : IDisposable
{
    private readonly List<ICommunicationObject> _opened = [];

    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(_opened))
        {
            try { item.Close(TimeSpan.FromSeconds(2)); }
            catch (Exception) { item.Abort(); }
        }
    }

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

    /// <summary>Hosts the service on a free local port with the gateway's binding and returns a connected client.</summary>
    private ILegacyBalanceService Start(IBalanceBackend backend)
    {
        var address = new Uri($"net.tcp://localhost:{FreePort()}/CashMovements/Balance");

        var host = new ServiceHost(new LegacyBalanceService(backend, _ => { }), address);
        host.AddServiceEndpoint(typeof(ILegacyBalanceService), GatewayBinding(), "");
        host.Open();
        _opened.Add(host);

        var factory = new ChannelFactory<ILegacyBalanceService>(GatewayBinding(), new EndpointAddress(address));
        _opened.Add(factory);

        var channel = factory.CreateChannel();
        _opened.Add((ICommunicationObject)channel);
        return channel;
    }

    /// <summary>The same binding as App.config's "WindowsTransport".</summary>
    private static NetTcpBinding GatewayBinding()
    {
        var binding = new NetTcpBinding(SecurityMode.Transport);
        binding.Security.Transport.ClientCredentialType = TcpClientCredentialType.Windows;
        binding.Security.Transport.ProtectionLevel = System.Net.Security.ProtectionLevel.EncryptAndSign;
        return binding;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
