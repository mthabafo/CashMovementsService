using CashMovements.Grpc.Interceptors;
using CashMovements.IntegrationTests.Support;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.IntegrationTests.Grpc;

/// <summary>
/// Structured logging through the real host: one summary per call (event 2001) with method, status and duration; every
/// log written during a call carries its GrpcMethod and CorrelationId scope; the caller's correlation ID is reused and
/// returned; and account IDs never appear unmasked.
/// </summary>
public sealed class LoggingTests : IDisposable
{
    private const string Account = "ACCOUNT-SECRET-9876";

    private readonly GrpcTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Every_call_logs_one_summary_with_method_status_and_duration()
    {
        await _host.Client.GetBalanceAsync(new Proto.GetBalanceRequest { AccountId = Account, Currency = "ZAR" });

        var summary = Assert.Single(Summaries());
        Assert.Equal(LogLevel.Information, summary.Level);
        Assert.Equal("GrpcCallCompleted", summary.EventId.Name);
        Assert.EndsWith("/GetBalance", (string)summary.Fields["GrpcMethod"]!);
        Assert.Equal(StatusCode.OK, summary.Fields["StatusCode"]);
        Assert.IsType<double>(summary.Fields["ElapsedMs"]);
    }

    [Fact]
    public async Task A_rejected_call_is_summarised_with_its_status_code()
    {
        await Assert.ThrowsAsync<RpcException>(async () =>
            await _host.Client.GetBalanceAsync(new Proto.GetBalanceRequest { AccountId = Account, Currency = "RAND" }));

        var summary = Assert.Single(Summaries());
        Assert.Equal(StatusCode.InvalidArgument, summary.Fields["StatusCode"]);
        Assert.Equal(LogLevel.Information, summary.Level); // the caller's mistake, not a server problem
    }

    [Fact]
    public async Task The_callers_correlation_id_is_used_for_every_log_of_the_call_and_returned()
    {
        var call = _host.Client.RecordMovementAsync(
            Request("REF-1"),
            new Metadata { { CallLoggingInterceptor.CorrelationHeader, "order-42.attempt_1" } });
        await call.ResponseAsync;

        Assert.Equal("order-42.attempt_1", call.GetTrailers().GetValue(CallLoggingInterceptor.CorrelationHeader));

        var recorded = Assert.Single(_host.Logs.Entries, e => e.EventId.Id == 1001);
        var summary = Assert.Single(Summaries());
        foreach (var entry in new[] { recorded, summary })
        {
            Assert.Equal("order-42.attempt_1", entry.Scope["CorrelationId"]);
            Assert.EndsWith("/RecordMovement", (string)entry.Scope["GrpcMethod"]!);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("has spaces")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")] // longer than 64
    public async Task A_missing_or_unsafe_correlation_id_is_replaced_with_a_generated_one(string? sent)
    {
        var headers = new Metadata();
        if (sent is not null)
            headers.Add(CallLoggingInterceptor.CorrelationHeader, sent);

        var call = _host.Client.GetBalanceAsync(new Proto.GetBalanceRequest { AccountId = Account, Currency = "ZAR" }, headers);
        await call.ResponseAsync;

        var returned = call.GetTrailers().GetValue(CallLoggingInterceptor.CorrelationHeader);
        Assert.Matches("^[0-9a-f]{32}$", returned);
        Assert.Equal(returned, Assert.Single(Summaries()).Scope["CorrelationId"]);
    }

    [Fact]
    public async Task A_statement_logs_StatementCompleted_inside_the_call_scope()
    {
        await _host.Client.RecordMovementAsync(Request("REF-1"));
        await _host.Client.RecordMovementAsync(Request("REF-2"));

        using var call = _host.Client.GetStatement(new Proto.GetStatementRequest
        {
            AccountId = Account,
            Currency = "ZAR",
            From = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-10)),
            To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await foreach (var _ in call.ResponseStream.ReadAllAsync())
        {
        }

        var completed = Assert.Single(_host.Logs.Entries, e => e.EventId.Id == 1101);
        Assert.Equal(2, completed.Fields["LineCount"]);
        Assert.EndsWith("/GetStatement", (string)completed.Scope["GrpcMethod"]!);
    }

    [Fact]
    public async Task No_log_entry_contains_the_raw_account_id()
    {
        await _host.Client.RecordMovementAsync(Request("REF-1"));
        await _host.Client.RecordMovementAsync(Request("REF-1"));                       // replay
        await Assert.ThrowsAsync<RpcException>(async () => await _host.Client.RecordMovementAsync(Request("REF-1", "99"))); // conflict
        await _host.Client.GetBalanceAsync(new Proto.GetBalanceRequest { AccountId = Account, Currency = "ZAR" });

        Assert.Contains(_host.Logs.Entries, e => e.EventId.Id == 1003); // the conflict was logged...
        Assert.All(_host.Logs.Entries, e => Assert.DoesNotContain(Account, e.AllText)); // ...and nothing names the account
    }

    private IEnumerable<CapturedLog> Summaries() => _host.Logs.Entries.Where(e => e.EventId.Id == 2001);

    private static Proto.RecordMovementRequest Request(string externalRef, string amount = "10")
        => new()
        {
            AccountId = Account,
            ExternalRef = externalRef,
            Amount = amount,
            Currency = "ZAR",
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-1)),
        };
}
