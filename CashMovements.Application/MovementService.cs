using CashMovements.Application.Interfaces;
using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;
using CashMovements.Domain.Interfaces;
using CashMovements.Domain.ReadModels;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.Application;

public sealed class MovementService : IMovementService
{
    private readonly IMovementRepository _repository;
    private readonly TimeProvider _clock;

    public MovementService(IMovementRepository repository, TimeProvider clock)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Balance> GetBalanceAsync(
        string accountId,
        string currency,
        CancellationToken cancellationToken)
    {
        var account = AccountId.From(accountId);
        var ccy = Currency.From(currency);

        var amount = await _repository.SumAsync(account, ccy, before: null, cancellationToken);

        return new Balance(account, ccy, amount, _clock.GetUtcNow());
    }

    public async Task<Statement> GetStatementAsync(
        string accountId,
        string currency,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var account = AccountId.From(accountId);
        var ccy = Currency.From(currency);

        // Fail fast before touching the database; Statement.Build enforces the same rule.
        if (from >= to)
            throw new InvalidValueException("Statement period start must be before its end.");

        var openingBalance = await _repository.SumAsync(account, ccy, before: from, cancellationToken);
        var movements = await _repository.ListAsync(account, ccy, from, to, cancellationToken);

        return Statement.Build(account, ccy, from, to, openingBalance, movements);
    }

    public async Task<Movement> RecordMovementAsync(
        Movement movement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movement);

        var existing = await _repository.GetByExternalReferenceAsync(
            movement.AccountId,
            movement.ExternalRef,
            cancellationToken);

        if (existing is not null)
        {
            if (existing.Amount != movement.Amount || existing.Currency != movement.Currency)
                throw new DuplicateMovementException(movement.ExternalRef);

            return existing;
        }

        return await _repository.AddAsync(movement, cancellationToken);
    }
}
