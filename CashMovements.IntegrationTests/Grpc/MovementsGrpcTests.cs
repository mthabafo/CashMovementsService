using CashMovements.IntegrationTests.Support;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Proto = CashMovements.Grpc.Contracts;
using ItemCase = CashMovements.Grpc.Contracts.GetStatementResponse.ItemOneofCase;

namespace CashMovements.IntegrationTests.Grpc;

/// <summary>
/// End-to-end over gRPC: generated client → real host (interceptor, mapper, service, domain) → in-memory repository.
/// The host uses the system clock, so all dates are relative to now and in the past.
/// </summary>
public sealed class MovementsGrpcTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly GrpcTestHost _host = new();

    private Proto.Movements.MovementsClient Client => _host.Client;

    public void Dispose() => _host.Dispose();

    // ---------- RecordMovement ----------

    [Fact]
    public async Task RecordMovement_returns_the_stored_movement_with_id_and_formatted_amount()
    {
        var response = await Client.RecordMovementAsync(Request("R1", "1000", daysAgo: 1, narration: "Salary"));

        Assert.Equal(1, response.Movement.Id);
        Assert.Equal("1000.00", response.Movement.Amount);
        Assert.Equal("ZAR", response.Movement.Currency);
        Assert.Equal("Salary", response.Movement.Narration);
    }

    [Fact]
    public async Task RecordMovement_retry_with_same_amount_and_currency_returns_the_original()
    {
        var first = await Client.RecordMovementAsync(Request("R1", "1000", daysAgo: 1));

        var retry = await Client.RecordMovementAsync(Request("R1", "1000.00", daysAgo: 1));

        Assert.Equal(first.Movement.Id, retry.Movement.Id);
    }

    [Fact]
    public async Task RecordMovement_same_ref_with_different_amount_is_already_exists()
    {
        await Client.RecordMovementAsync(Request("R1", "10", daysAgo: 1));

        var status = await StatusOf(async () => await Client.RecordMovementAsync(Request("R1", "11", daysAgo: 1)));

        Assert.Equal(StatusCode.AlreadyExists, status);
    }

    [Theory]
    [InlineData("1,5", "ZAR")]   // wrong decimal separator
    [InlineData("0", "ZAR")]     // zero amount
    [InlineData("1.005", "ZAR")] // too many decimal places
    [InlineData("5", "RAND")]    // not an ISO currency
    public async Task RecordMovement_invalid_input_is_invalid_argument(string amount, string currency)
    {
        var status = await StatusOf(async () =>
            await Client.RecordMovementAsync(Request("R1", amount, daysAgo: 1, currency: currency)));

        Assert.Equal(StatusCode.InvalidArgument, status);
    }

    [Fact]
    public async Task RecordMovement_without_occurred_at_is_invalid_argument()
    {
        var request = Request("R1", "1", daysAgo: 1);
        request.OccurredAt = null;

        var status = await StatusOf(async () => await Client.RecordMovementAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, status);
    }

    // ---------- GetBalance ----------

    [Fact]
    public async Task GetBalance_sums_movements_for_that_currency()
    {
        await Client.RecordMovementAsync(Request("R1", "1000", daysAgo: 20));
        await Client.RecordMovementAsync(Request("R2", "-149.50", daysAgo: 5));
        await Client.RecordMovementAsync(Request("R3", "999", daysAgo: 5, currency: "USD"));

        var balance = await Client.GetBalanceAsync(new Proto.GetBalanceRequest { AccountId = "ACC1", Currency = "zar" });

        Assert.Equal("850.50", balance.Amount);
        Assert.Equal("ZAR", balance.Currency);
    }

    [Fact]
    public async Task GetBalance_of_an_unknown_account_is_zero()
    {
        var balance = await Client.GetBalanceAsync(new Proto.GetBalanceRequest { AccountId = "NOPE", Currency = "ZAR" });

        Assert.Equal("0.00", balance.Amount);
    }

    // ---------- GetStatement (server streaming) ----------

    [Fact]
    public async Task GetStatement_streams_header_then_ordered_lines_then_footer()
    {
        await Client.RecordMovementAsync(Request("R1", "1000", daysAgo: 20)); // before the period → opening balance
        await Client.RecordMovementAsync(Request("R3", "50.5", daysAgo: 3));
        await Client.RecordMovementAsync(Request("R2", "-200", daysAgo: 5));  // recorded later, occurred earlier

        var items = await ReadStatement(fromDaysAgo: 10);

        Assert.Equal(new[] { ItemCase.Header, ItemCase.Line, ItemCase.Line, ItemCase.Footer }, items.Select(i => i.ItemCase));
        Assert.Equal("1000.00", items[0].Header.OpeningBalance);
        Assert.Equal(new[] { "R2", "R3" }, items.Where(i => i.ItemCase == ItemCase.Line).Select(i => i.Line.Movement.ExternalRef));
        Assert.Equal(new[] { "800.00", "850.50" }, items.Where(i => i.ItemCase == ItemCase.Line).Select(i => i.Line.RunningBalance));
        Assert.Equal("850.50", items[^1].Footer.ClosingBalance);
        Assert.Equal(2, items[^1].Footer.LineCount);
    }

    [Fact]
    public async Task GetStatement_with_no_movements_is_header_then_footer()
    {
        var items = await ReadStatement(fromDaysAgo: 1);

        Assert.Equal(new[] { ItemCase.Header, ItemCase.Footer }, items.Select(i => i.ItemCase));
        Assert.Equal("0.00", items[1].Footer.ClosingBalance);
        Assert.Equal(0, items[1].Footer.LineCount);
    }

    [Fact]
    public async Task GetStatement_with_a_reversed_period_is_invalid_argument()
    {
        var status = await StatusOf(() => ReadStatement(fromDaysAgo: -1));

        Assert.Equal(StatusCode.InvalidArgument, status);
    }

    [Fact]
    public async Task GetStatement_failing_mid_stream_ends_without_footer_and_hides_details()
    {
        await Client.RecordMovementAsync(Request("R1", "10", daysAgo: 3));
        await Client.RecordMovementAsync(Request("R2", "20", daysAgo: 2));
        _host.Repository.FailStreamAfterRows = 1;

        using var call = Client.GetStatement(StatementRequest(fromDaysAgo: 10));
        var received = new List<Proto.GetStatementResponse>();

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await foreach (var item in call.ResponseStream.ReadAllAsync())
                received.Add(item);
        });

        Assert.Equal(StatusCode.Internal, ex.StatusCode);
        Assert.DoesNotContain("secret", ex.Status.Detail);
        Assert.Equal(new[] { ItemCase.Header, ItemCase.Line }, received.Select(i => i.ItemCase));
    }

    // ---------- Helpers ----------

    private static Proto.RecordMovementRequest Request(
        string externalRef,
        string amount,
        int daysAgo,
        string currency = "ZAR",
        string? narration = null)
    {
        var request = new Proto.RecordMovementRequest
        {
            AccountId = "ACC1",
            ExternalRef = externalRef,
            Amount = amount,
            Currency = currency,
            OccurredAt = Timestamp.FromDateTimeOffset(Now.AddDays(-daysAgo)),
        };

        if (narration is not null)
            request.Narration = narration;

        return request;
    }

    private static Proto.GetStatementRequest StatementRequest(int fromDaysAgo)
        => new()
        {
            AccountId = "ACC1",
            Currency = "ZAR",
            From = Timestamp.FromDateTimeOffset(Now.AddDays(-fromDaysAgo)),
            To = Timestamp.FromDateTimeOffset(Now),
        };

    private async Task<List<Proto.GetStatementResponse>> ReadStatement(int fromDaysAgo)
    {
        using var call = Client.GetStatement(StatementRequest(fromDaysAgo));

        var items = new List<Proto.GetStatementResponse>();
        await foreach (var item in call.ResponseStream.ReadAllAsync())
            items.Add(item);

        return items;
    }

    private static async Task<StatusCode> StatusOf(Func<Task> call)
        => (await Assert.ThrowsAsync<RpcException>(call)).StatusCode;
}
