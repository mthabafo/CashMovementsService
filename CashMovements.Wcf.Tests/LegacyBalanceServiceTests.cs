using System.ServiceModel;
using CashMovements.Wcf.Contracts;
using Grpc.Core;

namespace CashMovements.Wcf.Tests;

public class LegacyBalanceServiceTests
{
    private readonly List<string> _log = [];

    private LegacyBalanceService Service(IBalanceBackend backend) => new(backend, _log.Add);

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
        Assert.Single(_log);
    }

    [Fact]
    public async Task Other_backend_errors_are_an_Internal_fault_without_details()
    {
        var fault = await FaultOf(FakeBalanceBackend.Failing(StatusCode.Internal, "secret stack trace"));

        Assert.Equal(BalanceFaultCodes.Internal, fault.Detail.Code);
        Assert.DoesNotContain("secret", fault.Detail.Message);
        Assert.Contains("secret", Assert.Single(_log)); // logged for the operator instead
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

    private Task<FaultException<BalanceFault>> FaultOf(IBalanceBackend backend)
        => Assert.ThrowsAsync<FaultException<BalanceFault>>(() => Service(backend).GetBalanceAsync("ACC-1", "ZAR"));
}
