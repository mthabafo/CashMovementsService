using System.ServiceModel;

namespace CashMovements.Wcf.Contracts;

/// <summary>
/// The legacy gateway's WCF contract. Shared with clients as source (CashMovements.Wcf.Client links these files),
/// and also published as metadata on the host's mex endpoint for clients that generate proxies.
/// </summary>
[ServiceContract(Namespace = ContractNamespace.Value, Name = "LegacyBalanceService")]
public interface ILegacyBalanceService
{
    /// <summary>
    /// The current balance of <paramref name="accountId"/> in <paramref name="currency"/> (ISO 4217, e.g. "ZAR").
    /// An account with no movements has a balance of 0.
    /// </summary>
    /// <remarks>
    /// Failures are returned as a <see cref="FaultException{BalanceFault}"/>; see <see cref="BalanceFaultCodes"/>.
    /// The WCF operation name is "GetBalance"; the Async suffix only reflects the Task-based .NET signature.
    /// </remarks>
    [OperationContract]
    [FaultContract(typeof(BalanceFault))]
    Task<BalanceResult> GetBalanceAsync(string accountId, string currency);
}

internal static class ContractNamespace
{
    public const string Value = "urn:cashmovements:legacy:v1";
}
