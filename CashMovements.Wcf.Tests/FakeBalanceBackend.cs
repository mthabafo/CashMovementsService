using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.Wcf.Tests;

/// <summary>Stands in for the gRPC service: returns a fixed balance or fails the way the backend would.</summary>
internal sealed class FakeBalanceBackend : IBalanceBackend
{
    private readonly Func<string, string, Proto.GetBalanceResponse> _respond;

    private FakeBalanceBackend(Func<string, string, Proto.GetBalanceResponse> respond) => _respond = respond;

    public static readonly DateTime AsAt = new(2026, 11, 19, 10, 30, 0, DateTimeKind.Utc);

    public List<(string AccountId, string Currency)> Calls { get; } = [];

    public static FakeBalanceBackend Returning(string amount)
        => new((account, currency) => new Proto.GetBalanceResponse
        {
            AccountId = account,
            Currency = currency.ToUpperInvariant(),
            Amount = amount,
            AsAt = Timestamp.FromDateTime(AsAt),
        });

    public static FakeBalanceBackend Failing(StatusCode code, string detail)
        => new((_, _) => throw new RpcException(new Status(code, detail)));

    public static FakeBalanceBackend Throwing(Exception exception)
        => new((_, _) => throw exception);

    public async Task<Proto.GetBalanceResponse> GetBalanceAsync(string accountId, string currency)
    {
        Calls.Add((accountId, currency));
        await Task.Yield();
        return _respond(accountId, currency);
    }
}
