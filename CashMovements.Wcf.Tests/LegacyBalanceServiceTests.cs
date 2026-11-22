using System.ServiceModel;
using CashMovements.Wcf.Contracts;
using CashMovements.Wcf.Tests.Support;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace CashMovements.Wcf.Tests;

public class LegacyBalanceServiceTests
{
    private readonly CapturingLogger<LegacyBalanceService> _logger = new();

    private LegacyBalanceService Service(IBalanceBackend backend) => new(backend, _logger);

    [Fact]
    public async Task Maps_the_backend_balance_to_the_wcf_result()
    {
        var result = await Service(FakeBalanceBackend.Returning("-249.50")).GetBalanceAsync("ACC-1", "zar");

        Assert.Equal("ACC-1", result.AccountId);
        Assert.Equal("ZAR", result.Currency);
        Assert.Equal(-249.50m, result.Amount);
        Assert.Equal(FakeBalanceBackend.AsAt, result.AsAtUtc);
        Assert.Equal(DateTimeKind.Utc, result.AsAtUtc.Kind);
    }

    [Fact]
    public async Task Amounts_are_parsed_independently_of_the_machine_culture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE"); // ',' decimal separator

            var result = await Service(FakeBalanceBackend.Returning("1234.56")).GetBalanceAsync("ACC-1", "ZAR");

            Assert.Equal(1234.56m, result.Amount);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Passes_the_inputs_to_the_backend_unchanged_and_nulls_as_empty()
    {
        var backend = FakeBalanceBackend.Returning("1.00");

        await Service(backend).GetBalanceAsync(" ACC-1 ", "zar");
        await Service(backend).GetBalanceAsync(null!, null!);

        Assert.Equal((" ACC-1 ", "zar"), backend.Calls[0]); // validation and normalisation belong to the backend
        Assert.Equal(("", ""), backend.Calls[1]);
    }

    [Fact]
    public async Task Invalid_input_is_an_InvalidArgument_fault_with_the_backend_message()
    {
        var fault = await FaultOf(FakeBalanceBackend.Failing(StatusCode.InvalidArgument, "Currency must be a 3-letter ISO 4217 code."));

        Assert.Equal(BalanceFaultCodes.InvalidArgument, fault.Detail.Code);
        Assert.Equal("Currency must be a 3-letter ISO 4217 code.", fault.Detail.Message);
        Assert.Equal(BalanceFaultCodes.InvalidArgument, fault.Code.Name);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task Backend_down_or_slow_is_a_BackendUnavailable_fault(StatusCode status)
    {
        var fault = await FaultOf(FakeBalanceBackend.Failing(status, "connection refused to 10.0.0.5"));

        Assert.Equal(BalanceFaultCodes.BackendUnavailable, fault.Detail.Code);
        Assert.DoesNotContain("10.0.0.5", fault.Detail.Message);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(4003, entry.EventId.Id); // BackendUnavailable
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(status.ToString(), entry.Fields["StatusCode"]);
    }

    [Fact]
    public async Task Other_backend_errors_are_an_Internal_fault_without_details()
    {
        var fault = await FaultOf(FakeBalanceBackend.Failing(StatusCode.Internal, "secret stack trace"));

        Assert.Equal(BalanceFaultCodes.Internal, fault.Detail.Code);
        Assert.DoesNotContain("secret", fault.Detail.Message);
        var entry = Assert.Single(_logger.Entries); // logged for the operator instead
        Assert.Equal(4004, entry.EventId.Id);      // BalanceFailed
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("secret", entry.Exception!.Message);
    }

    [Fact]
    public async Task An_unparseable_amount_from_the_backend_is_an_Internal_fault()
    {
        var fault = await FaultOf(FakeBalanceBackend.Returning("12,50"));

        Assert.Equal(BalanceFaultCodes.Internal, fault.Detail.Code);
    }

    [Fact]
    public async Task Unexpected_exceptions_are_an_Internal_fault()
    {
        var fault = await FaultOf(FakeBalanceBackend.Throwing(new InvalidOperationException("secret")));

        Assert.Equal(BalanceFaultCodes.Internal, fault.Detail.Code);
        Assert.DoesNotContain("secret", fault.Detail.Message);
    }

    // ---------- Logging ----------

    [Fact]
    public async Task Each_call_sends_a_new_correlation_id_to_the_backend_and_logs_under_it()
    {
        var backend = FakeBalanceBackend.Returning("1.00");

        await Service(backend).GetBalanceAsync("ACC-1001", "ZAR");
        await Service(backend).GetBalanceAsync("ACC-1001", "ZAR");

        Assert.Equal(2, backend.CorrelationIds.Distinct().Count());
        Assert.All(backend.CorrelationIds, id => Assert.Matches("^[0-9a-f]{32}$", id));
        Assert.Equal(backend.CorrelationIds, _logger.Entries.Select(e => e.Scope["CorrelationId"]));
        Assert.All(_logger.Entries, e => Assert.Equal("GetBalance", e.Scope["Operation"]));
    }

    [Fact]
    public async Task A_returned_balance_is_logged_with_the_account_masked_and_no_amount()
    {
        await Service(FakeBalanceBackend.Returning("39600.01")).GetBalanceAsync("ACC-1001", "zar");

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(4001, entry.EventId.Id); // BalanceReturned
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("****1001", entry.Fields["Account"]);
        Assert.Equal("ZAR", entry.Fields["Currency"]);
        Assert.DoesNotContain("ACC-1001", entry.AllText);
        Assert.DoesNotContain("39600", entry.AllText);
    }

    [Fact]
    public async Task Invalid_input_is_logged_as_rejected_with_the_reason()
    {
        await FaultOf(FakeBalanceBackend.Failing(StatusCode.InvalidArgument, "Currency is required."));

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(4002, entry.EventId.Id); // BalanceRejected
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("Currency is required.", entry.Fields["Reason"]);
    }

    [Fact]
    public async Task No_log_entry_ever_contains_the_raw_account_id()
    {
        var backends = new IBalanceBackend[]
        {
            FakeBalanceBackend.Returning("1.00"),
            FakeBalanceBackend.Failing(StatusCode.InvalidArgument, "bad"),
            FakeBalanceBackend.Failing(StatusCode.Unavailable, "down"),
            FakeBalanceBackend.Throwing(new InvalidOperationException("boom")),
        };

        foreach (var backend in backends)
        {
            try { await Service(backend).GetBalanceAsync("ACCOUNT-SECRET-1234", "ZAR"); }
            catch (FaultException<BalanceFault>) { }
        }

        Assert.Equal(4, _logger.Entries.Count);
        Assert.All(_logger.Entries, e => Assert.DoesNotContain("ACCOUNT-SECRET", e.AllText));
    }

    private Task<FaultException<BalanceFault>> FaultOf(IBalanceBackend backend)
        => Assert.ThrowsAsync<FaultException<BalanceFault>>(() => Service(backend).GetBalanceAsync("ACC-1", "ZAR"));
}
