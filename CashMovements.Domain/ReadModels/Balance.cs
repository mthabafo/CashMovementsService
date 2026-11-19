using CashMovements.Domain.ValueObjects;

namespace CashMovements.Domain.ReadModels;

/// <summary>
/// The balance of one account in one currency, derived from its movements (never stored).
/// An account with no movements has a balance of zero.
/// </summary>
/// <param name="Amount">Sum of all signed movement amounts. May be negative (overdrafts are allowed).</param>
/// <param name="AsAt">The moment the balance was calculated (UTC).</param>
public sealed record Balance(AccountId AccountId, Currency Currency, decimal Amount, DateTimeOffset AsAt);
