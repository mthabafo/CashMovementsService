using System.Runtime.CompilerServices;
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

        var amount = await _repository.SumAmountsAsync(account, ccy, cancellationToken);

        return new Balance(account, ccy, amount, _clock.GetUtcNow());
    }

    public async Task<StatementStream> GetStatementAsync(
        string accountId,
        string currency,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var account = AccountId.From(accountId);
        var ccy = Currency.From(currency);

        // Fail fast before touching the database.
        StatementHeader.EnsureValidPeriod(from, to);

        var openingBalance = await _repository.SumAmountsBeforeAsync(account, ccy, from, cancellationToken);
        var header = StatementHeader.Create(account, ccy, from, to, openingBalance);

        return new StatementStream(header, StreamLinesAsync(header, cancellationToken));
    }

    public async Task<Movement> RecordMovementAsync(
        Movement movement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movement);

        var existing = await _repository.FindByExternalRefAsync(
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

    private async IAsyncEnumerable<StatementLine> StreamLinesAsync(
        StatementHeader header,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var calculator = new StatementCalculator(header);

        var movements = _repository.StreamInPeriodAsync(
            header.AccountId, header.Currency, header.From, header.To, cancellationToken);

        await foreach (var movement in movements.WithCancellation(cancellationToken))
            yield return calculator.Add(movement);
    }
}
