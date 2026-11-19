using CashMovements.Domain.Interfaces;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.IntegrationTests.Support;

/// <summary>
/// Runs the real CashMovements.Grpc host in memory (same Program.cs, interceptor and DI wiring) and exposes a
/// generated gRPC client. By default the repository is swapped for <see cref="InMemoryMovementRepository"/>;
/// pass a connection string to keep the real SQL Server repository instead.
/// </summary>
internal sealed class GrpcTestHost : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly GrpcChannel _channel;
    private readonly InMemoryMovementRepository? _repository;

    public GrpcTestHost(string? sqlConnectionString = null)
    {
        if (sqlConnectionString is null)
            _repository = new InMemoryMovementRepository();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (sqlConnectionString is not null)
                builder.UseSetting("ConnectionStrings:CashMovements", sqlConnectionString);

            if (_repository is not null)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IMovementRepository>();
                    services.AddSingleton<IMovementRepository>(_repository);
                });
            }
        });

        _channel = GrpcChannel.ForAddress(
            _factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });

        Client = new Proto.Movements.MovementsClient(_channel);
    }

    /// <summary>The in-memory repository (only when the host was not given a SQL Server connection string).</summary>
    public InMemoryMovementRepository Repository
        => _repository ?? throw new InvalidOperationException("This host uses SQL Server, not the in-memory repository.");

    public Proto.Movements.MovementsClient Client { get; }

    public void Dispose()
    {
        _channel.Dispose();
        _factory.Dispose();
    }
}
