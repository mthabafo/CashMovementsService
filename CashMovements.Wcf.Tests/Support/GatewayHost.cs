using System.Net;
using System.Net.Sockets;
using System.ServiceModel;
using CashMovements.Wcf.Contracts;

namespace CashMovements.Wcf.Tests.Support;

/// <summary>
/// Hosts the WCF gateway on a free local port with its real binding (NetTcp, Transport security, Windows credentials,
/// as the current user) and hands out connected clients. Disposing closes everything it opened.
/// </summary>
internal sealed class GatewayHost : IDisposable
{
    private readonly List<ICommunicationObject> _opened = [];

    /// <summary>Messages the service logged.</summary>
    public List<string> Log { get; } = [];

    /// <summary>Hosts <see cref="LegacyBalanceService"/> over <paramref name="backend"/> and returns a client for it.</summary>
    public ILegacyBalanceService Start(IBalanceBackend backend)
    {
        var address = new Uri($"net.tcp://localhost:{FreePort()}/CashMovements/Balance");

        var host = new ServiceHost(new LegacyBalanceService(backend, message => { lock (Log) Log.Add(message); }), address);
        host.AddServiceEndpoint(typeof(ILegacyBalanceService), Binding(), "");
        host.Open();
        _opened.Add(host);

        var factory = new ChannelFactory<ILegacyBalanceService>(Binding(), new EndpointAddress(address));
        _opened.Add(factory);

        var channel = factory.CreateChannel();
        _opened.Add((ICommunicationObject)channel);
        return channel;
    }

    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(_opened))
        {
            try { item.Close(TimeSpan.FromSeconds(2)); }
            catch (Exception) { item.Abort(); }
        }
    }

    /// <summary>The same binding as App.config's "WindowsTransport".</summary>
    private static NetTcpBinding Binding()
    {
        var binding = new NetTcpBinding(SecurityMode.Transport);
        binding.Security.Transport.ClientCredentialType = TcpClientCredentialType.Windows;
        binding.Security.Transport.ProtectionLevel = System.Net.Security.ProtectionLevel.EncryptAndSign;
        return binding;
    }

    /// <summary>A local TCP port that nothing is listening on right now.</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
