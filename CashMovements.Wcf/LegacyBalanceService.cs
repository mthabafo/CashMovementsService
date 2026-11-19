using System.Globalization;
using System.ServiceModel;
using CashMovements.Wcf.Contracts;
using Grpc.Core;

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
    private readonly Action<string> _log;

    public LegacyBalanceService(IBalanceBackend backend, Action<string> log)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<BalanceResult> GetBalanceAsync(string accountId, string currency)
    {
        try
        {
            var response = await _backend.GetBalanceAsync(accountId ?? "", currency ?? "");

            return new BalanceResult
            {
                AccountId = response.AccountId,
                Currency = response.Currency,
                Amount = decimal.Parse(response.Amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
                AsAtUtc = response.AsAt.ToDateTime(),
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            // The backend's message describes the input problem and is safe to pass on.
            throw Fault(BalanceFaultCodes.InvalidArgument, ex.Status.Detail);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            _log($"Backend unavailable for GetBalance({accountId}, {currency}): {ex.StatusCode} {ex.Status.Detail}");
            throw Fault(BalanceFaultCodes.BackendUnavailable, "The balance service is not available. Try again later.");
        }
        catch (Exception ex)
        {
            _log($"GetBalance({accountId}, {currency}) failed: {ex}");
            throw Fault(BalanceFaultCodes.Internal, "An unexpected error occurred.");
        }
    }

    private static FaultException<BalanceFault> Fault(string code, string message)
        => new(new BalanceFault { Code = code, Message = message }, new FaultReason(message), new FaultCode(code));
}
