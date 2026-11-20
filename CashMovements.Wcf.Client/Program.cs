using System.Diagnostics;
using System.Globalization;
using System.ServiceModel;
using CashMovements.Wcf.Contracts;

// Legacy client for the WCF gateway.
//   CashMovements.Wcf.Client.exe [--address <net.tcp url>] [--wait]                        interactive prompt
//   CashMovements.Wcf.Client.exe <account> <currency> [--address <net.tcp url>] [--wait]   one balance, then exit
// The endpoint comes from App.config ("LegacyBalance"); --address overrides its address.
// --wait retries for up to 30 seconds while the gateway or its backend is still starting (first call only).
// Exit codes (one-off): 0 success, 1 the call failed, 2 bad command line.

const string Usage = """
    Usage:
      CashMovements.Wcf.Client.exe [--address <net.tcp url>] [--wait]                        interactive prompt
      CashMovements.Wcf.Client.exe <account> <currency> [--address <net.tcp url>] [--wait]   one balance, then exit

      <account>    e.g. ACC-1001
      <currency>   ISO 4217 code, e.g. ZAR
      --address    gateway address (default from App.config: net.tcp://localhost:8095/CashMovements/Balance)
      --wait       retry for up to 30 seconds while the gateway or the gRPC service is still starting
    """;

const string PromptHelp = """
    Type <account> <currency> to get a balance, e.g. ACC-1001 ZAR.
    Other commands: help, clear, exit.
    """;

// Under the Visual Studio debugger the window waits for Enter before closing (see Exit), so output stays readable.

var positional = new List<string>();
string? address = null;
var wait = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--help" or "-h" or "/?":
            Console.WriteLine(Usage);
            return Exit(0);
        case "--wait":
            wait = true;
            break;
        case "--address" when i + 1 < args.Length:
            address = args[++i];
            break;
        default:
            if (args[i].StartsWith("--", StringComparison.Ordinal))
                return Exit(UsageError($"Unknown option {args[i]}."));
            positional.Add(args[i]);
            break;
    }
}

if (positional.Count is not (0 or 2))
    return Exit(UsageError("Expected <account> and <currency>, or nothing for the interactive prompt."));

using var factory = address is null
    ? new ChannelFactory<ILegacyBalanceService>("LegacyBalance")
    : new ChannelFactory<ILegacyBalanceService>("LegacyBalance", new EndpointAddress(address));

var target = factory.Endpoint.Address.Uri;

// ---------- One-off ----------

if (positional.Count == 2)
    return Exit(await ShowBalanceAsync(positional[0], positional[1]) ? 0 : 1);

// ---------- Interactive ----------

Console.WriteLine($"CashMovements legacy client. Gateway: {target}");
Console.WriteLine(PromptHelp);

while (true)
{
    Console.WriteLine();
    Console.Write("cashmovements.wcf> ");
    var input = Console.ReadLine();
    if (input is null)
    {
        Console.WriteLine();
        Console.WriteLine("Input was closed, so the prompt can't read anything. Exiting.");
        return Exit(0);
    }

    var words = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (words.Length == 0)
        continue;

    switch (words[0].ToLowerInvariant())
    {
        case "exit" or "quit":
            return 0;
        case "help" or "?":
            Console.WriteLine(PromptHelp);
            continue;
        case "cls" or "clear":
            Console.Clear();
            continue;
    }

    // "balance ACC-1001 ZAR" is accepted too, to match the gRPC client.
    if (words.Length == 3 && words[0].Equals("balance", StringComparison.OrdinalIgnoreCase))
        words = [words[1], words[2]];

    if (words.Length != 2)
    {
        Console.Error.WriteLine("Type <account> <currency>, e.g. ACC-1001 ZAR (or 'help').");
        continue;
    }

    await ShowBalanceAsync(words[0], words[1]);
}

// ---------- Helpers ----------

// Calls the gateway and prints the balance or the error. Returns true on success.
async Task<bool> ShowBalanceAsync(string account, string currency)
{
    var giveUpAt = DateTime.UtcNow.AddSeconds(wait ? 30 : 0);

    while (true)
    {
        var channel = factory.CreateChannel();
        var clientChannel = (IClientChannel)channel;

        try
        {
            var balance = await channel.GetBalanceAsync(account, currency);
            clientChannel.Close();
            wait = false; // everything is up; later calls report failures straight away

            Console.WriteLine($"  Account: {balance.AccountId}");
            Console.WriteLine($"  Balance: {balance.Amount.ToString("0.00", CultureInfo.InvariantCulture)} {balance.Currency}");
            Console.WriteLine($"  As at:   {balance.AsAtUtc:yyyy-MM-dd HH:mm:ss}Z");
            return true;
        }
        catch (FaultException<BalanceFault> fault)
        {
            clientChannel.Abort();
            if (fault.Detail.Code == BalanceFaultCodes.BackendUnavailable && KeepWaiting(giveUpAt, "the gRPC service"))
                continue;

            Console.Error.WriteLine($"{fault.Detail.Code}: {fault.Detail.Message}");
            return false;
        }
        catch (EndpointNotFoundException)
        {
            clientChannel.Abort();
            if (KeepWaiting(giveUpAt, "the gateway"))
                continue;

            Console.Error.WriteLine($"Could not reach the gateway at {target}. Is CashMovements.Wcf running?");
            return false;
        }
        catch (Exception ex) when (ex is CommunicationException or TimeoutException)
        {
            clientChannel.Abort();
            Console.Error.WriteLine($"Call failed: {ex.Message}");
            return false;
        }
    }
}

static bool KeepWaiting(DateTime giveUpAt, string what)
{
    if (DateTime.UtcNow >= giveUpAt)
        return false;

    Console.WriteLine($"  Waiting for {what}...");
    Thread.Sleep(TimeSpan.FromSeconds(1));
    return true;
}

// Under the debugger, keeps the window open until Enter so the output can be read before it closes.
static int Exit(int code)
{
    if (Debugger.IsAttached && !Console.IsInputRedirected)
    {
        Console.WriteLine();
        Console.WriteLine("Press Enter to close this window.");
        Console.ReadLine();
    }

    return code;
}

static int UsageError(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine(Usage);
    return 2;
}
