using CashMovements.Domain.Entities;

namespace CashMovements.Domain.ReadModels;

/// <summary>One statement row: a movement plus the account's running balance after it.</summary>
public sealed record StatementLine(Movement Movement, decimal RunningBalance);
