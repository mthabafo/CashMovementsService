using Google.Protobuf.WellKnownTypes;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.Grpc.Client;

/// <summary>Formats responses for the console. Amounts are printed exactly as the server sent them.</summary>
internal static class Output
{
    public static string Time(Timestamp? timestamp)
        => timestamp is null ? "-" : timestamp.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss'Z'");

    public static void Movement(Proto.Movement m)
    {
        Console.WriteLine($"  Id:          {m.Id}");
        Console.WriteLine($"  Account:     {m.AccountId}");
        Console.WriteLine($"  ExternalRef: {m.ExternalRef}");
        Console.WriteLine($"  Amount:      {m.Amount} {m.Currency}");
        Console.WriteLine($"  OccurredAt:  {Time(m.OccurredAt)}");
        Console.WriteLine($"  Narration:   {(m.HasNarration ? m.Narration : "-")}");
    }

    public static void Balance(Proto.GetBalanceResponse b)
        => Console.WriteLine($"  {b.AccountId}: {b.Amount} {b.Currency} (as at {Time(b.AsAt)})");

    public static void StatementHeader(Proto.StatementHeader h)
    {
        Console.WriteLine($"  Statement {h.AccountId} {h.Currency}, {Time(h.From)} to {Time(h.To)} (end exclusive)");
        Console.WriteLine($"  Opening balance: {h.OpeningBalance}");
        Console.WriteLine();
        Console.WriteLine($"  {"Occurred (UTC)",-20}  {"ExternalRef",-16}  {"Amount",14}  {"Running",14}  Narration");
        Console.WriteLine($"  {new string('-', 20)}  {new string('-', 16)}  {new string('-', 14)}  {new string('-', 14)}  ---------");
    }

    public static void StatementLine(Proto.StatementLine line)
    {
        var m = line.Movement;
        Console.WriteLine(
            $"  {Time(m.OccurredAt),-20}  {Truncate(m.ExternalRef, 16),-16}  {m.Amount,14}  {line.RunningBalance,14}  {(m.HasNarration ? m.Narration : "")}");
    }

    public static void StatementFooter(Proto.StatementFooter f)
    {
        Console.WriteLine();
        Console.WriteLine($"  Closing balance: {f.ClosingBalance} ({f.LineCount} line{(f.LineCount == 1 ? "" : "s")})");
    }

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..(length - 1)] + "~";
}
