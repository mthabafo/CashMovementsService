using System.Diagnostics;
using System.Globalization;
using System.ServiceModel;
using CashMovements.Wcf.Contracts;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace CashMovements.Wcf;

/// <summary>
/// The WCF implementation: forwards to the backend and translates the result. Validation and balance rules live in
/// the backend, so this gateway can't drift from the gRPC API. Every failure becomes a typed
/// <see cref="FaultException{BalanceFault}"/>; internal details are logged, never returned.
/// </summary>
/// <remarks>Stateless and thread-safe, so one instance serves all calls concurrently.</remarks>
[ServiceBehavior(
    Namespace = "urn:cashmovements:legacy:v1",
    InstanceContextMode = InstanceContextMode.Single,
    ConcurrencyMode = ConcurrencyMode.Multiple)]
public sealed class LegacyBalanceService : ILegacyBalanceService
{
    private readonly IBalanceBackend _backend;
    private readonly ILogger<LegacyBalanceService> _logger;

    public LegacyBalanceService(IBalanceBackend backend, ILogger<LegacyBalanceService> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BalanceResult> GetBalanceAsync(string accountId, string currency)
    {
        // One ID per call, logged here and sent to the gRPC server, which logs its side of the call under the same ID.
        var correlationId = Guid.NewGuid().ToString("N");
        var account = GatewayLog.MaskAccount(accountId);
        var stopwatch = Stopwatch.StartNew();

        using var scope = _logger.BeginScope(new LogScope(("Operation", "GetBalance"), ("CorrelationId", correlationId)));

        try
        {
            var response = await _backend.GetBalanceAsync(accountId ?? "", currency ?? "", correlationId);

            var result = new BalanceResult
            {
                AccountId = response.AccountId,
                Currency = response.Currency,
                Amount = decimal.Parse(response.Amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
                AsAtUtc = response.AsAt.ToDateTime(),
            };

            GatewayLog.BalanceReturned(_logger, account, result.Currency, stopwatch.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            // The backend's message describes the input problem and is safe to pass on.
            GatewayLog.BalanceRejected(_logger, account, ex.Status.Detail);
            throw Fault(BalanceFaultCodes.InvalidArgument, ex.Status.Detail);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            GatewayLog.BackendUnavailable(_logger, account, ex.StatusCode.ToString(), ex.Status.Detail, stopwatch.Elapsed.TotalMilliseconds);
            throw Fault(BalanceFaultCodes.BackendUnavailable, "The balance service is not available. Try again later.");
        }
        catch (Exception ex)
        {
            GatewayLog.BalanceFailed(_logger, ex, account);
            throw Fault(BalanceFaultCodes.Internal, "An unexpected error occurred.");
        }
    }

    private static FaultException<BalanceFault> Fault(string code, string message)
        => new(new BalanceFault { Code = code, Message = message }, new FaultReason(message), new FaultCode(code));
}
