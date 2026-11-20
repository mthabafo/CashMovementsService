using System.Configuration;
using System.ServiceModel;
using CashMovements.Wcf;

// Self-hosted WCF gateway. Endpoints, binding and security are in App.config (system.serviceModel);
// the backend address and timeout are in appSettings. Runs until Ctrl+C.

var backendUrl = ConfigurationManager.AppSettings["GrpcBackendUrl"];
if (string.IsNullOrWhiteSpace(backendUrl) || !Uri.TryCreate(backendUrl, UriKind.Absolute, out var backendUri))
{
    return Fail("appSettings 'GrpcBackendUrl' must be the gRPC-Web address of the CashMovements service, e.g. http://localhost:5146.");
}

var timeoutSeconds = int.TryParse(ConfigurationManager.AppSettings["BackendTimeoutSeconds"], out var seconds) && seconds > 0 ? seconds : 10;

void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");

using var backend = new GrpcBalanceBackend(backendUri, TimeSpan.FromSeconds(timeoutSeconds));
var host = new ServiceHost(new LegacyBalanceService(backend, Log));

try
{
    host.Open();
}
catch (Exception ex) when (ex is AddressAlreadyInUseException or CommunicationException or InvalidOperationException)
{
    return Fail($"Could not start the WCF host: {ex.Message}");
}

Log("CashMovements legacy WCF gateway started.");
foreach (var endpoint in host.Description.Endpoints)
    Log($"  {endpoint.Contract.Name}: {endpoint.Address.Uri}");
Log($"  Backend (gRPC-Web): {backendUri}");
Log("Press Ctrl+C to stop.");

using var stop = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Set();
};
stop.Wait();

Log("Stopping...");
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
