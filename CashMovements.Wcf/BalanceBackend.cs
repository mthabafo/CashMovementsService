using System.Net;
using System.Net.Http;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.Wcf;

/// <summary>Where the gateway gets balances from. An interface so the WCF service can be tested without a backend.</summary>
public interface IBalanceBackend
{
    /// <summary>Calls the backend's GetBalance. Failures surface as <see cref="RpcException"/>.</summary>
    Task<Proto.GetBalanceResponse> GetBalanceAsync(string accountId, string currency);
}

/// <summary>
/// Calls the CashMovements gRPC service. .NET Framework can't use HTTP/2 without TLS, so this speaks gRPC-Web over
/// HTTP/1.1, which the server accepts on its gRPC-Web port (5146 by default).
/// </summary>
public sealed class GrpcBalanceBackend : IBalanceBackend, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly Proto.Movements.MovementsClient _client;
    private readonly TimeSpan _timeout;

    public GrpcBalanceBackend(Uri address, TimeSpan timeout)
    {
        var handler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler())
        {
            HttpVersion = HttpVersion.Version11,
        };

        _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        _client = new Proto.Movements.MovementsClient(_channel);
        _timeout = timeout;
    }

    public async Task<Proto.GetBalanceResponse> GetBalanceAsync(string accountId, string currency)
        => await _client.GetBalanceAsync(
            new Proto.GetBalanceRequest { AccountId = accountId, Currency = currency },
            deadline: DateTime.UtcNow.Add(_timeout));

    public void Dispose() => _channel.Dispose();
}
