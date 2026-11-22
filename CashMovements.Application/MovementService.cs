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
            return ResolveResubmission(existing, movement);

        try
        {
            return await _repository.AddAsync(movement, cancellationToken);
        }
        catch (DuplicateMovementException)
        {
            // Another request recorded this (account, ref) between the lookup above and this insert, and the database's
            // unique key rejected ours. That row is committed now, so treat this request like any other re-submission:
            // identical simultaneous retries all get the original movement back.
            var winner = await _repository.FindByExternalRefAsync(
                movement.AccountId,
                movement.ExternalRef,
                cancellationToken);

            if (winner is null)
                throw; // Not expected: movements are never deleted.

            return ResolveResubmission(winner, movement);
        }
    }

    /// <summary>
    /// A re-submission of an existing (account, ref): the same amount and currency is a retry and returns the original
    /// movement; anything else is a genuine duplicate.
    /// </summary>
    private static Movement ResolveResubmission(Movement existing, Movement submitted)
        => existing.Amount == submitted.Amount && existing.Currency == submitted.Currency
            ? existing
            : throw new DuplicateMovementException(submitted.ExternalRef);

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
