using CashMovements.IntegrationTests.Support;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Proto = CashMovements.Grpc.Contracts;
using ItemCase = CashMovements.Grpc.Contracts.GetStatementResponse.ItemOneofCase;

namespace CashMovements.IntegrationTests.Grpc;

/// <summary>
/// The whole stack with nothing swapped: generated client → gRPC host (Program.cs wiring) → service →
/// MovementRepository → SQL Server. Detailed behaviour is covered by the in-memory gRPC tests and the repository tests;
/// this proves the pieces are wired together correctly.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class MovementsGrpcSqlServerTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly GrpcTestHost _host;
    private readonly string _account = "G" + Guid.NewGuid().ToString("N");

    public MovementsGrpcSqlServerTests(SqlServerFixture db) => _host = new GrpcTestHost(db.ConnectionString);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Record_retry_duplicate_balance_and_statement_work_against_sql_server()
    {
        var client = _host.Client;

        var first = await client.RecordMovementAsync(Request("R1", "1000", daysAgo: 20));
        var retry = await client.RecordMovementAsync(Request("R1", "1000.00", daysAgo: 20));
        await client.RecordMovementAsync(Request("R2", "-200", daysAgo: 5));
        await client.RecordMovementAsync(Request("R3", "50.5", daysAgo: 3));
        var duplicate = await Assert.ThrowsAsync<RpcException>(
            async () => await client.RecordMovementAsync(Request("R1", "999", daysAgo: 20)));

        var balance = await client.GetBalanceAsync(new Proto.GetBalanceRequest { AccountId = _account, Currency = "ZAR" });

        using var call = client.GetStatement(new Proto.GetStatementRequest
        {
            AccountId = _account,
            Currency = "ZAR",
            From = Timestamp.FromDateTimeOffset(Now.AddDays(-10)),
            To = Timestamp.FromDateTimeOffset(Now),
        });
        var items = new List<Proto.GetStatementResponse>();
        await foreach (var item in call.ResponseStream.ReadAllAsync())
            items.Add(item);

        Assert.True(first.Movement.Id > 0);
        Assert.Equal(first.Movement.Id, retry.Movement.Id);
        Assert.Equal(StatusCode.AlreadyExists, duplicate.StatusCode);
        Assert.Equal("850.50", balance.Amount);
        Assert.Equal(new[] { ItemCase.Header, ItemCase.Line, ItemCase.Line, ItemCase.Footer }, items.Select(i => i.ItemCase));
        Assert.Equal("1000.00", items[0].Header.OpeningBalance);
        Assert.Equal(new[] { "800.00", "850.50" }, items.Where(i => i.ItemCase == ItemCase.Line).Select(i => i.Line.RunningBalance));
        Assert.Equal("850.50", items[^1].Footer.ClosingBalance);
    }

    private Proto.RecordMovementRequest Request(string externalRef, string amount, int daysAgo)
        => new()
        {
            AccountId = _account,
            ExternalRef = externalRef,
            Amount = amount,
            Currency = "ZAR",
            OccurredAt = Timestamp.FromDateTimeOffset(Now.AddDays(-daysAgo)),
        };
}
