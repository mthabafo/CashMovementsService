using System.Globalization;
using System.ServiceModel;
using CashMovements.Wcf.Contracts;

// Legacy client for the WCF gateway: prints the current balance of one account.
//   CashMovements.Wcf.Client.exe <account> <currency> [--address <net.tcp url>] [--wait]
// The endpoint comes from App.config ("LegacyBalance"); --address overrides its address.
// --wait keeps retrying for up to 30 seconds while the gateway or its backend is still starting.
// Exit codes: 0 success, 1 the call failed, 2 bad command line.

const string Usage = """
    Usage: CashMovements.Wcf.Client.exe <account> <currency> [--address <net.tcp url>] [--wait]

      <account>    e.g. ACC-1001
      <currency>   ISO 4217 code, e.g. ZAR
      --address    gateway address (default from App.config: net.tcp://localhost:8095/CashMovements/Balance)
      --wait       retry for up to 30 seconds while the gateway or the gRPC service is still starting
    """;

var positional = new List<string>();
string? address = null;
var wait = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--help" or "-h" or "/?":
            Console.WriteLine(Usage);
            return 0;
        case "--wait":
            wait = true;
            break;
        case "--address" when i + 1 < args.Length:
            address = args[++i];
            break;
        default:
            if (args[i].StartsWith("--", StringComparison.Ordinal))
                return UsageError($"Unknown option {args[i]}.");
            positional.Add(args[i]);
            break;
    }
}

if (positional.Count != 2)
    return UsageError("Expected <account> and <currency>.");

using var factory = address is null
    ? new ChannelFactory<ILegacyBalanceService>("LegacyBalance")
    : new ChannelFactory<ILegacyBalanceService>("LegacyBalance", new EndpointAddress(address));

var target = factory.Endpoint.Address.Uri;
var giveUpAt = DateTime.UtcNow.AddSeconds(wait ? 30 : 0);

while (true)
{
    var channel = factory.CreateChannel();
    var clientChannel = (IClientChannel)channel;

    try
    {
        var balance = await channel.GetBalanceAsync(positional[0], positional[1]);
        clientChannel.Close();

        Console.WriteLine($"  Account: {balance.AccountId}");
        Console.WriteLine($"  Balance: {balance.Amount.ToString("0.00", CultureInfo.InvariantCulture)} {balance.Currency}");
        Console.WriteLine($"  As at:   {balance.AsAtUtc:yyyy-MM-dd HH:mm:ss}Z");
        return 0;
    }
    catch (FaultException<BalanceFault> fault)
    {
        clientChannel.Abort();
        if (fault.Detail.Code == BalanceFaultCodes.BackendUnavailable && KeepWaiting("the gRPC service"))
            continue;

        Console.Error.WriteLine($"{fault.Detail.Code}: {fault.Detail.Message}");
        return 1;
    }
    catch (EndpointNotFoundException)
    {
        clientChannel.Abort();
        if (KeepWaiting("the gateway"))
            continue;

        Console.Error.WriteLine($"Could not reach the gateway at {target}. Is CashMovements.Wcf running?");
        return 1;
    }
    catch (Exception ex) when (ex is CommunicationException or TimeoutException)
    {
        clientChannel.Abort();
        Console.Error.WriteLine($"Call failed: {ex.Message}");
        return 1;
    }
}

bool KeepWaiting(string what)
{
    if (DateTime.UtcNow >= giveUpAt)
        return false;

    Console.WriteLine($"  Waiting for {what}...");
    Thread.Sleep(TimeSpan.FromSeconds(1));
    return true;
}

static int UsageError(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine(Usage);
    return 2;
}
