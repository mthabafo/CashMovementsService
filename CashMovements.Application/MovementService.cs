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

        var source = await _repository.OpenStatementAsync(account, ccy, from, to, cancellationToken);
        try
        {
            var header = StatementHeader.Create(account, ccy, from, to, source.OpeningBalance);

            return new StatementStream(header, StreamLinesAsync(header, source.Movements, cancellationToken), source);
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
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

    private static async IAsyncEnumerable<StatementLine> StreamLinesAsync(
        StatementHeader header,
        IAsyncEnumerable<Movement> movements,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var calculator = new StatementCalculator(header);

        await foreach (var movement in movements.WithCancellation(cancellationToken))
            yield return calculator.Add(movement);
    }
}
