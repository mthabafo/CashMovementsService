using CashMovements.Domain.Interfaces;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.IntegrationTests.Support;

/// <summary>
/// Runs the real CashMovements.Grpc host in memory (same Program.cs, interceptor and DI wiring) with the
/// repository swapped for <see cref="InMemoryMovementRepository"/>, and exposes a generated gRPC client.
/// </summary>
internal sealed class GrpcTestHost : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly GrpcChannel _channel;

    public GrpcTestHost()
    {
        Repository = new InMemoryMovementRepository();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMovementRepository>();
                services.AddSingleton<IMovementRepository>(Repository);
            }));

        _channel = GrpcChannel.ForAddress(
            _factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });

        Client = new Proto.Movements.MovementsClient(_channel);
    }

    public InMemoryMovementRepository Repository { get; }

    public Proto.Movements.MovementsClient Client { get; }

    public void Dispose()
    {
        _channel.Dispose();
        _factory.Dispose();
    }
}
