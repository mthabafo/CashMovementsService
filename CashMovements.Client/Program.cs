using CashMovements.Client;
using Grpc.Core;
using Grpc.Net.Client;
using Proto = CashMovements.Grpc.Contracts;

// Command-line client for manually testing the CashMovements gRPC service. See README.md.
// Exit codes: 0 success, 1 the server returned an error (or demo found something unexpected), 2 bad command line.

const string DefaultServer = "http://localhost:5145";

const string Usage = """
    Usage: dotnet run --project CashMovements.Client -- <command> [arguments] [--server <url>]

    Commands:
      balance   <account> <currency>
      record    <account> <ref> <amount> <currency> [--at <time>] [--narration <text>]
      statement <account> <currency> [--from <time>] [--to <time>]
      demo      [<account>]

    Amounts are sent exactly as typed: signed, '.' as decimal separator (e.g. 100, -249.50).
    Times without an offset are local time; a bare date means midnight (e.g. 2026-11-01, 2026-11-01T14:30, 2026-11-01T12:30Z).
    record --at defaults to now; statement defaults to the 30 days before --to (default now).
    --server defaults to http://localhost:5145 (the server's "http" launch profile).

    Examples:
      balance ACC-1001 ZAR
      record ACC-1001 PAY-77 -149.50 ZAR --narration "Groceries"
      statement ACC-1001 ZAR --from 2026-10-01 --to 2026-11-01
      demo
    """;

CommandLine line;
try
{
    line = CommandLine.Parse(args);
}
catch (UsageException ex)
{
    return UsageError(ex.Message);
}

if (line.Command is null || line.HelpRequested || line.Command is "help")
{
    Console.WriteLine(Usage);
    return line.Command is null && !line.HelpRequested ? 2 : 0;
}

var server = line.Option("server") ?? DefaultServer;
if (!Uri.TryCreate(server, UriKind.Absolute, out var serverUri))
    return UsageError($"--server '{server}' is not a URL.");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let the call end cleanly instead of killing the process
    cancellation.Cancel();
};

using var channel = GrpcChannel.ForAddress(serverUri);
var commands = new Commands(new Proto.Movements.MovementsClient(channel), cancellation.Token);

try
{
    return line.Command.ToLowerInvariant() switch
    {
        "balance" => await commands.BalanceAsync(line),
        "record" => await commands.RecordAsync(line),
        "statement" => await commands.StatementAsync(line),
        "demo" => await commands.DemoAsync(line),
        _ => UsageError($"Unknown command '{line.Command}'."),
    };
}
catch (UsageException ex)
{
    return UsageError(ex.Message);
}
catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
{
    Console.Error.WriteLine($"Unavailable: could not reach {serverUri}. Is the server running? ({ex.Status.Detail})");
    return 1;
}
catch (RpcException ex)
{
    Console.Error.WriteLine($"{ex.StatusCode}: {ex.Status.Detail}");
    return 1;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled.");
    return 1;
}

static int UsageError(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine("Run with --help for usage.");
    return 2;
}
