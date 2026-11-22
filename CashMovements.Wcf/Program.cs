using System.Configuration;
using System.ServiceModel;
using CashMovements.Wcf;
using Microsoft.Extensions.Logging;

// Self-hosted WCF gateway. Endpoints, binding and security are in App.config (system.serviceModel);
// the backend address, timeout and log format are in appSettings. Runs until Ctrl+C.

var backendUrl = ConfigurationManager.AppSettings["GrpcBackendUrl"];
if (string.IsNullOrWhiteSpace(backendUrl) || !Uri.TryCreate(backendUrl, UriKind.Absolute, out var backendUri))
{
    return Fail("appSettings 'GrpcBackendUrl' must be the gRPC-Web address of the CashMovements service, e.g. http://localhost:5146.");
}

var timeoutSeconds = int.TryParse(ConfigurationManager.AppSettings["BackendTimeoutSeconds"], out var seconds) && seconds > 0 ? seconds : 10;

// Structured logs: readable single lines by default, JSON (one object per line, with scopes) when LogFormat is "json".
var jsonLogs = string.Equals(ConfigurationManager.AppSettings["LogFormat"], "json", StringComparison.OrdinalIgnoreCase);
using var loggerFactory = LoggerFactory.Create(logging =>
{
    logging.SetMinimumLevel(LogLevel.Information);
    if (jsonLogs)
    {
        logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        });
    }
    else
    {
        logging.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
    }
});
var log = loggerFactory.CreateLogger("CashMovements.Wcf");

using var backend = new GrpcBalanceBackend(backendUri, TimeSpan.FromSeconds(timeoutSeconds));
var host = new ServiceHost(new LegacyBalanceService(backend, loggerFactory.CreateLogger<LegacyBalanceService>()));

try
{
    host.Open();
}
catch (Exception ex) when (ex is AddressAlreadyInUseException or CommunicationException or InvalidOperationException)
{
    return Fail($"Could not start the WCF host: {ex.Message}");
}

log.LogInformation("CashMovements legacy WCF gateway started.");
foreach (var endpoint in host.Description.Endpoints)
    log.LogInformation("Endpoint {Contract}: {Address}", endpoint.Contract.Name, endpoint.Address.Uri);
log.LogInformation("Backend (gRPC-Web): {BackendUrl}", backendUri);
log.LogInformation("Press Ctrl+C to stop.");

using var stop = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Set();
};
stop.Wait();

log.LogInformation("Stopping...");
try
{
    host.Close(TimeSpan.FromSeconds(5));
}
catch (Exception)
{
    host.Abort();
}

return 0;

// Prints a startup error; under the Visual Studio debugger, keeps the window open until Enter so it can be read.
static int Fail(string message)
{
    Console.Error.WriteLine(message);

    if (System.Diagnostics.Debugger.IsAttached && !Console.IsInputRedirected)
    {
        Console.WriteLine("Press Enter to close this window.");
        Console.ReadLine();
    }

    return 1;
}
