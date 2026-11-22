using CashMovements.Domain.Interfaces;
using System.Net;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
    private readonly List<GrpcChannel> _extraChannels = [];

    public GrpcTestHost(string? sqlConnectionString = null)
    {
        if (sqlConnectionString is null)
            _repository = new InMemoryMovementRepository();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Tests own their database; never touch the developer's local one.
            builder.UseSetting("Database:AutoSetup", "false");

            // Everything the host logs is captured, so tests can check log events, fields and scopes.
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));

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

    /// <summary>Every log entry the host has written.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>The in-memory repository (only when the host was not given a SQL Server connection string).</summary>
    public InMemoryMovementRepository Repository
        => _repository ?? throw new InvalidOperationException("This host uses SQL Server, not the in-memory repository.");

    public Proto.Movements.MovementsClient Client { get; }

    /// <summary>A client that speaks gRPC-Web over HTTP/1.1, as the .NET Framework WCF gateway does.</summary>
    public Proto.Movements.MovementsClient CreateGrpcWebClient()
    {
        var handler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, _factory.Server.CreateHandler())
        {
            HttpVersion = HttpVersion.Version11,
        };
        var channel = GrpcChannel.ForAddress(_factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler });
        _extraChannels.Add(channel);
        return new Proto.Movements.MovementsClient(channel);
    }

    public void Dispose()
    {
        foreach (var channel in _extraChannels)
            channel.Dispose();
        _channel.Dispose();
        _factory.Dispose();
    }
}
