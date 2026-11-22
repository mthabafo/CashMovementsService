using System.Runtime.CompilerServices;
using CashMovements.Application.Interfaces;
using CashMovements.Application.Logging;
using CashMovements.Domain.Entities;
using CashMovements.Domain.Exceptions;
using CashMovements.Domain.Interfaces;
using CashMovements.Domain.ReadModels;
using CashMovements.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashMovements.Application;

public sealed class MovementService : IMovementService
{
    private readonly IMovementRepository _repository;
    private readonly TimeProvider _clock;
    private readonly ILogger<MovementService> _logger;

    /// <param name="logger">Business events (see <see cref="ApplicationLog"/>); none are logged if omitted.</param>
    public MovementService(IMovementRepository repository, TimeProvider clock, ILogger<MovementService>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? NullLogger<MovementService>.Instance;
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
            return ResolveResubmission(existing, movement, lostInsertRace: false);

        try
        {
            var recorded = await _repository.AddAsync(movement, cancellationToken);

            ApplicationLog.MovementRecorded(
                _logger, recorded.Id, LogMask.Account(recorded.AccountId.Value), recorded.ExternalRef.Value, recorded.Currency.Code);

            return recorded;
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

            return ResolveResubmission(winner, movement, lostInsertRace: true);
        }
    }

    /// <summary>
    /// A re-submission of an existing (account, ref): the same amount and currency is a retry and returns the original
    /// movement; anything else is a genuine duplicate.
    /// </summary>
    private Movement ResolveResubmission(Movement existing, Movement submitted, bool lostInsertRace)
    {
        var account = LogMask.Account(submitted.AccountId.Value);
        var externalRef = submitted.ExternalRef.Value;

        var differs = new List<string>(2);
        if (existing.Amount != submitted.Amount)
            differs.Add("amount");
        if (existing.Currency != submitted.Currency)
            differs.Add("currency");

        if (differs.Count > 0)
        {
            ApplicationLog.MovementConflict(_logger, externalRef, account, string.Join(" and ", differs));
            throw new DuplicateMovementException(submitted.ExternalRef);
        }

        ApplicationLog.MovementReplayed(_logger, externalRef, account, existing.Id, lostInsertRace);
        return existing;
    }

    /// <summary>
    /// Turns movements into statement lines with running balances, one at a time, and logs how the statement ended:
    /// completed, or ended early because the caller cancelled, a row failed, or the caller stopped reading.
    /// </summary>
    private async IAsyncEnumerable<StatementLine> StreamLinesAsync(
        StatementHeader header,
        IAsyncEnumerable<Movement> movements,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var calculator = new StatementCalculator(header);
        var started = _clock.GetTimestamp();
        string? endedEarly = "stopped"; // until proven otherwise: the caller stopped reading before the end

        try
        {
            // Enumerated by hand because "yield return" isn't allowed inside a try block that has a catch.
            await using var enumerator = movements.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                StatementLine line;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        endedEarly = null;
                        yield break;
                    }

                    line = calculator.Add(enumerator.Current);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    endedEarly = "cancelled";
                    throw;
                }
                catch
                {
                    endedEarly = "failed"; // the cause is logged where the error is reported
                    throw;
                }

                yield return line;
            }
        }
        finally
        {
            var account = LogMask.Account(header.AccountId.Value);
            var elapsedMs = _clock.GetElapsedTime(started).TotalMilliseconds;

            if (endedEarly is null)
                ApplicationLog.StatementCompleted(_logger, account, header.Currency.Code, header.From, header.To, calculator.LineCount, elapsedMs);
            else
                ApplicationLog.StatementEndedEarly(_logger, account, header.Currency.Code, header.From, header.To, endedEarly, calculator.LineCount, elapsedMs);
        }
    }
}
