using CashMovements.Application;
using CashMovements.Application.Interfaces;
using CashMovements.Domain.Interfaces;
using CashMovements.Grpc.Interceptors;
using CashMovements.Grpc.Services;
using CashMovements.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc(options => options.Interceptors.Add<DomainExceptionInterceptor>());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IMovementRepository, MovementRepository>();
builder.Services.AddScoped<IMovementService, MovementService>();

var app = builder.Build();

app.MapGrpcService<MovementGrpcService>();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();

// Exposes the entry point to WebApplicationFactory<Program> in tests.
public partial class Program
{
}
