using System.Runtime.Serialization;

namespace CashMovements.Wcf.Contracts;

/// <summary>Details of a failed call, carried by <c>FaultException&lt;BalanceFault&gt;</c>.</summary>
[DataContract(Namespace = ContractNamespace.Value)]
public sealed class BalanceFault
{
    /// <summary>One of <see cref="BalanceFaultCodes"/>.</summary>
    [DataMember(Order = 1, IsRequired = true)]
    public string Code { get; set; } = "";

    /// <summary>Human-readable explanation, safe to show to a user.</summary>
    [DataMember(Order = 2, IsRequired = true)]
    public string Message { get; set; } = "";
}

/// <summary>The values of <see cref="BalanceFault.Code"/>.</summary>
public static class BalanceFaultCodes
{
    /// <summary>The account ID or currency is not valid. Retrying won't help.</summary>
    public const string InvalidArgument = "InvalidArgument";

    /// <summary>The balance service could not be reached or didn't answer in time. Retrying later may succeed.</summary>
    public const string BackendUnavailable = "BackendUnavailable";

    /// <summary>Anything unexpected. Details are logged by the gateway, not returned.</summary>
    public const string Internal = "Internal";
}
