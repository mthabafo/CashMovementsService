using System.Text;
using CashMovements.Grpc.Client;
using Grpc.Core;
using Grpc.Net.Client;
using Proto = CashMovements.Grpc.Contracts;

// Command-line client for manually testing the CashMovements gRPC service. See README.md.
// With no command it starts an interactive prompt; with a command it runs it once and exits.
// Exit codes (one-shot): 0 success, 1 the server returned an error (or demo found something unexpected), 2 bad command line.

const string DefaultServer = "http://localhost:5145";
const string Prompt = "cashmovements.grpc> ";

const string CommandHelp = """
    Commands:
      balance   <account> <currency>
      record    <account> <ref> <amount> <currency> [--at <time>] [--narration <text>]
      statement <account> <currency> [--from <time>] [--to <time>]
      export    <account> <currency> [--from <time>] [--to <time>] [--out <path>]
      demo      [<account>]

    Amounts are sent exactly as typed: signed, '.' as decimal separator (e.g. 100, -249.50).
    Times without an offset are local time; a bare date means midnight (e.g. 2026-11-01, 2026-11-01T14:30, 2026-11-01T12:30Z).
    record --at defaults to now; statement and export default to the 30 days before --to (default now).
    export saves a CSV (with a progress bar) to --out, a file or folder; by default the server's file name here.
    Quote values with spaces: --narration "Card payment".

    Examples:
      balance ACC-1001 ZAR
      record ACC-1001 PAY-77 -149.50 ZAR --narration "Groceries"
      statement ACC-1001 ZAR --from 2026-10-01 --to 2026-12-01
      export ACC-VOLUME ZAR --from 2026-01-01      (150,000 lines, seeded in development)
      demo
    """;

const string Usage = $"""
    Usage:
      CashMovements.Grpc.Client [--server <url>]                          interactive prompt
      CashMovements.Grpc.Client <command> [arguments] [--server <url>]    run one command and exit

    --server defaults to {DefaultServer} (the gRPC server's HTTP/2 endpoint, Kestrel:Endpoints:Grpc in its appsettings.json).

    {CommandHelp}
    """;

CommandLine startup;
try
{
    startup = CommandLine.Parse(args);
}
catch (UsageException ex)
{
    return UsageError(ex.Message);
}

if (startup.HelpRequested || startup.Command is "help")
{
    Console.WriteLine(Usage);
    return 0;
}

var server = startup.Option("server") ?? DefaultServer;
if (!Uri.TryCreate(server, UriKind.Absolute, out var serverUri))
    return UsageError($"--server '{server}' is not a URL.");

// Ctrl+C cancels the command that is running; with nothing running it exits as usual.
CancellationTokenSource? running = null;
Console.CancelKeyPress += (_, e) =>
{
    var current = running;
    if (current is null)
        return;

    e.Cancel = true;
    current.Cancel();
};

using var channel = GrpcChannel.ForAddress(serverUri);
var client = new Proto.Movements.MovementsClient(channel);

if (startup.Command is not null)
{
    // The demo is often launched together with the server (Visual Studio), so give the server time to start.
    if (startup.Command.Equals("demo", StringComparison.OrdinalIgnoreCase))
        await WaitForServerAsync();

    return await RunAsync(startup);
}

// ---------- Interactive ----------

Console.WriteLine($"CashMovements client. Server: {serverUri}");
await WaitForServerAsync();
Console.WriteLine("Type 'help' for commands, 'exit' to quit.");

while (true)
{
    Console.WriteLine();
    Console.Write(Prompt);
    var input = Console.ReadLine();
    if (input is null)
        break; // end of input (Ctrl+Z / Ctrl+D, or stdin closed)

    string[] tokens;
    try
    {
        tokens = Tokenize(input);
    }
    catch (UsageException ex)
    {
        Console.Error.WriteLine(ex.Message);
        continue;
    }

    if (tokens.Length == 0)
        continue;

    switch (tokens[0].ToLowerInvariant())
    {
        case "exit" or "quit":
            return 0;
        case "help" or "?":
            Console.WriteLine(CommandHelp);
            continue;
        case "cls" or "clear":
            Console.Clear();
            continue;
    }

    try
    {
        await RunAsync(CommandLine.Parse(tokens));
    }
    catch (UsageException ex)
    {
        Console.Error.WriteLine(ex.Message);
    }
}

return 0;

// ---------- Helpers ----------

// Runs one command and reports errors; returns the exit code.
async Task<int> RunAsync(CommandLine line)
{
    using var cancellation = new CancellationTokenSource();
    running = cancellation;
    var commands = new Commands(client, cancellation.Token);

    try
    {
        return line.Command!.ToLowerInvariant() switch
        {
            "balance" => await commands.BalanceAsync(line),
            "record" => await commands.RecordAsync(line),
            "statement" => await commands.StatementAsync(line),
            "export" => await commands.ExportAsync(line),
            "demo" => await commands.DemoAsync(line),
            _ => UsageError($"Unknown command '{line.Command}'."),
        };
    }
    catch (UsageException ex)
    {
        return UsageError(ex.Message);
    }
    catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellation.IsCancellationRequested)
    {
        Console.Error.WriteLine("Cancelled.");
        return 1;
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
    finally
    {
        running = null;
    }
}

// Waits briefly for the server, which may still be starting (e.g. launched together from Visual Studio).
async Task WaitForServerAsync()
{
    Console.Write("Connecting... ");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        await channel.ConnectAsync(timeout.Token);
        Console.WriteLine("connected.");
    }
    catch (Exception ex) when (ex is OperationCanceledException or RpcException or InvalidOperationException)
    {
        Console.WriteLine("not reachable after 30 seconds. Commands will fail until the server is running.");
    }
}

// Splits a typed line into arguments like a shell: whitespace-separated, double quotes group words.
static string[] Tokenize(string input)
{
    var tokens = new List<string>();
    var current = new StringBuilder();
    var inQuotes = false;
    var hasToken = false;

    foreach (var c in input)
    {
        if (c == '"')
        {
            inQuotes = !inQuotes;
            hasToken = true;
        }
        else if (char.IsWhiteSpace(c) && !inQuotes)
        {
            if (hasToken)
                tokens.Add(current.ToString());
            current.Clear();
            hasToken = false;
        }
        else
        {
            current.Append(c);
            hasToken = true;
        }
    }

    if (inQuotes)
        throw new UsageException("Missing closing quote.");
    if (hasToken)
        tokens.Add(current.ToString());

    return [.. tokens];
}

static int UsageError(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine("Type 'help' (or run with --help) for usage.");
    return 2;
}
