using System.Runtime.Serialization;

namespace CashMovements.Wcf.Contracts;

/// <summary>A balance as returned by <see cref="ILegacyBalanceService.GetBalanceAsync"/>.</summary>
[DataContract(Namespace = ContractNamespace.Value)]
public sealed class BalanceResult
{
    [DataMember(Order = 1, IsRequired = true)]
    public string AccountId { get; set; } = "";

    /// <summary>ISO 4217 code, upper case.</summary>
    [DataMember(Order = 2, IsRequired = true)]
    public string Currency { get; set; } = "";

    /// <summary>Signed: negative when the account is overdrawn. Two decimal places.</summary>
    [DataMember(Order = 3, IsRequired = true)]
    public decimal Amount { get; set; }

    /// <summary>When the balance was calculated (UTC).</summary>
    [DataMember(Order = 4, IsRequired = true)]
    public DateTime AsAtUtc { get; set; }
}
