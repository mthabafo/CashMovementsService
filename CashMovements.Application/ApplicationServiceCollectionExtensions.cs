using CashMovements.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CashMovements.Application;

/// <summary>
/// Registers the application layer. <see cref="MovementService"/> also needs an <c>IMovementRepository</c> (registered by
/// the infrastructure layer) and a <see cref="TimeProvider"/> (registered by the host).
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IMovementService, MovementService>();
        return services;
    }
}
