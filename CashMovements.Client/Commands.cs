using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Proto = CashMovements.Grpc.Contracts;
using ItemCase = CashMovements.Grpc.Contracts.GetStatementResponse.ItemOneofCase;

namespace CashMovements.Client;

/// <summary>One method per command. Each returns the process exit code; gRPC errors propagate to Program.</summary>
internal sealed class Commands(Proto.Movements.MovementsClient client, CancellationToken cancellationToken)
{
    private static readonly TimeSpan UnaryDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StreamDeadline = TimeSpan.FromMinutes(5);

    // balance <account> <currency>
    public async Task<int> BalanceAsync(CommandLine line)
    {
        var account = line.Required(0, "account");
        var currency = line.Required(1, "currency");
        line.EnsureNothingElse(2);

        Output.Balance(await GetBalanceAsync(account, currency));
        return 0;
    }

    // record <account> <ref> <amount> <currency> [--at <time>] [--narration <text>]
    public async Task<int> RecordAsync(CommandLine line)
    {
        var account = line.Required(0, "account");
        var externalRef = line.Required(1, "ref");
        var amount = line.Required(2, "amount");
        var currency = line.Required(3, "currency");
        var at = line.TimeOption("at") ?? DateTimeOffset.UtcNow;
        var narration = line.Option("narration");
        line.EnsureNothingElse(4);

        var movement = await RecordMovementAsync(account, externalRef, amount, currency, at, narration);

        Console.WriteLine("  Recorded (or an earlier identical request returned):");
        Output.Movement(movement);
        return 0;
    }

    // statement <account> <currency> [--from <time>] [--to <time>]
    public async Task<int> StatementAsync(CommandLine line)
    {
        var account = line.Required(0, "account");
        var currency = line.Required(1, "currency");
        var to = line.TimeOption("to") ?? DateTimeOffset.UtcNow;
        var from = line.TimeOption("from") ?? to.AddDays(-30);
        line.EnsureNothingElse(2);

        var footer = await StreamStatementAsync(account, currency, from, to);
        return footer is null ? 1 : 0;
    }

    // demo [<account>]
    public async Task<int> DemoAsync(CommandLine line)
    {
        var account = line.Optional(0) ?? $"DEMO-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        line.EnsureNothingElse(1);

        var now = DateTimeOffset.UtcNow;
        var failures = 0;

        void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "OK" : "UNEXPECTED")}] {what}");
            if (!ok)
                failures++;
        }

        Console.WriteLine($"Demo on account {account} (ZAR)");
        Console.WriteLine();

        Console.WriteLine("1. Record two movements");
        var salary = await RecordMovementAsync(account, "DEMO-1", "1000", "ZAR", now.AddDays(-3), "Salary");
        var rent = await RecordMovementAsync(account, "DEMO-2", "-249.50", "ZAR", now.AddDays(-1), "Rent");
        Check(salary.Id > 0 && rent.Id > 0, $"recorded Ids {salary.Id} and {rent.Id}");

        Console.WriteLine("2. Retry DEMO-1 with the same amount (written differently)");
        var retry = await RecordMovementAsync(account, "DEMO-1", "1000.00", "ZAR", now.AddDays(-3), "Salary");
        Check(retry.Id == salary.Id, $"retry returned Id {retry.Id}, the original is {salary.Id}");

        Console.WriteLine("3. Reuse DEMO-1 with a different amount");
        try
        {
            await RecordMovementAsync(account, "DEMO-1", "999", "ZAR", now.AddDays(-3), null);
            Check(false, "the server accepted it");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            Check(true, $"rejected with {ex.StatusCode}: {ex.Status.Detail}");
        }

        Console.WriteLine("4. Record an invalid amount");
        try
        {
            await RecordMovementAsync(account, "DEMO-3", "1,5", "ZAR", now, null);
            Check(false, "the server accepted it");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            Check(true, $"rejected with {ex.StatusCode}: {ex.Status.Detail}");
        }

        Console.WriteLine("5. Balance");
        var balance = await GetBalanceAsync(account, "ZAR");
        Output.Balance(balance);
        Check(balance.Amount == "750.50", "balance is 750.50");

        Console.WriteLine("6. Statement for the last 7 days");
        var footer = await StreamStatementAsync(account, "ZAR", now.AddDays(-7), DateTimeOffset.UtcNow);
        Check(footer is { LineCount: 2, ClosingBalance: "750.50" }, "2 lines, closing balance 750.50");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "Demo passed." : $"Demo finished with {failures} unexpected result(s).");
        return failures == 0 ? 0 : 1;
    }

    private async Task<Proto.GetBalanceResponse> GetBalanceAsync(string account, string currency)
        => await client.GetBalanceAsync(
            new Proto.GetBalanceRequest { AccountId = account, Currency = currency },
            deadline: DateTime.UtcNow.Add(UnaryDeadline),
            cancellationToken: cancellationToken);

    private async Task<Proto.Movement> RecordMovementAsync(
        string account, string externalRef, string amount, string currency, DateTimeOffset at, string? narration)
    {
        var request = new Proto.RecordMovementRequest
        {
            AccountId = account,
            ExternalRef = externalRef,
            Amount = amount, // sent exactly as typed, so invalid input can be tried
            Currency = currency,
            OccurredAt = Timestamp.FromDateTimeOffset(at),
        };
        if (narration is not null)
            request.Narration = narration;

        var response = await client.RecordMovementAsync(
            request,
            deadline: DateTime.UtcNow.Add(UnaryDeadline),
            cancellationToken: cancellationToken);

        return response.Movement;
    }

    /// <summary>Prints each item as it arrives. Returns the footer, or null if the stream ended without one.</summary>
    private async Task<Proto.StatementFooter?> StreamStatementAsync(
        string account, string currency, DateTimeOffset from, DateTimeOffset to)
    {
        using var call = client.GetStatement(
            new Proto.GetStatementRequest
            {
                AccountId = account,
                Currency = currency,
                From = Timestamp.FromDateTimeOffset(from),
                To = Timestamp.FromDateTimeOffset(to),
            },
            deadline: DateTime.UtcNow.Add(StreamDeadline),
            cancellationToken: cancellationToken);

        Proto.StatementFooter? footer = null;

        await foreach (var item in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            switch (item.ItemCase)
            {
                case ItemCase.Header:
                    Output.StatementHeader(item.Header);
                    break;
                case ItemCase.Line:
                    Output.StatementLine(item.Line);
                    break;
                case ItemCase.Footer:
                    footer = item.Footer;
                    Output.StatementFooter(item.Footer);
                    break;
            }
        }

        if (footer is null)
            Console.Error.WriteLine("  The stream ended without a footer: the statement is incomplete.");

        return footer;
    }
}
