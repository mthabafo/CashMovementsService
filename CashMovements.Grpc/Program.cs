using CashMovements.Application;
using CashMovements.Grpc.Interceptors;
using CashMovements.Grpc.Services;
using CashMovements.Infrastructure;
using CashMovements.Infrastructure.Persistence;

// Composition root. This is the only API code allowed to use CashMovements.Infrastructure (enforced by
// ArchitectureTests): API → Application → Domain ← Infrastructure.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc(options =>
{
    // Order matters: the first runs outermost. Call logging sees the final status, after exceptions are translated.
    options.Interceptors.Add<CallLoggingInterceptor>();
    options.Interceptors.Add<DomainExceptionInterceptor>();
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration); // fails at startup if the connection string is missing

var app = builder.Build();

// Local development convenience (appsettings.Development.json): create the database if it's missing and run the
// repo's idempotent scripts (schema, procedures, seed), so there are no manual setup steps. Off unless configured.
if (app.Configuration.GetValue<bool>("Database:AutoSetup"))
{
    var connectionString = app.Configuration.GetConnectionString(InfrastructureServiceCollectionExtensions.ConnectionStringName)!;
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseSetup");
    var scripts = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Database"), "*.sql").Order().ToList();

    var created = await DatabaseSetup.EnsureDatabaseExistsAsync(connectionString);
    await DatabaseSetup.RunScriptsAsync(connectionString, scripts);

    logger.LogInformation(
        "Database {State}; ran {Scripts}.",
        created ? "created" : "already existed",
        string.Join(", ", scripts.Select(Path.GetFileName)));
}

// Ports are set in appsettings.json (Kestrel:Endpoints): 5145 is gRPC over HTTP/2; 5146 is HTTP/1.1 for gRPC-Web,
// which clients that can't do HTTP/2 without TLS use, such as the .NET Framework WCF gateway (CashMovements.Wcf).
app.UseGrpcWeb();
app.MapGrpcService<MovementGrpcService>().EnableGrpcWeb();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();

// Exposes the entry point to WebApplicationFactory<Program> in tests.
public partial class Program
{
}
