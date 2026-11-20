using CashMovements.Domain.Interfaces;
using CashMovements.Infrastructure.Persistence;
using CashMovements.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CashMovements.Infrastructure;

/// <summary>
/// Registers the infrastructure layer (SQL Server data access). The host calls only this, so no other API code needs
/// to know which classes implement the domain's interfaces.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Name of the connection string in configuration: <c>ConnectionStrings:CashMovements</c>.</summary>
    public const string ConnectionStringName = "CashMovements";

    /// <exception cref="InvalidOperationException">The connection string is missing, so startup fails rather than the first request.</exception>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured.");

        services.AddSingleton<ISqlConnectionFactory>(new SqlConnectionFactory(connectionString));
        services.AddScoped<IMovementRepository, MovementRepository>();
        return services;
    }
}
