using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Retry;

namespace CashMovements.Infrastructure.Persistence;

/// <summary>
/// Retries a database operation after a transient failure (a lost or failed connection, a deadlock), with exponential
/// backoff and jitter, so brief blips never reach the caller. Retrying stops when the caller cancels (including a gRPC
/// deadline passing), after <see cref="SqlRetryOptions.MaxRetries"/>, or once
/// <see cref="SqlRetryOptions.MaxRetryDuration"/> has passed; the last failure is then rethrown unchanged.
/// <para>
/// Each attempt must be self-contained (open its own connection), and only operations that are safe to repeat may be
/// passed in: reads, and the insert, which the external reference makes idempotent.
/// </para>
/// </summary>
public sealed class SqlRetryPolicy
{
    private static readonly EventId RetryEvent = new(3001, "DatabaseRetry");
    private static readonly ResiliencePropertyKey<long> StartedAt = new("CashMovements.SqlRetry.StartedAt");

    private readonly ResiliencePipeline _pipeline;
    private readonly TimeProvider _time;
    private readonly TimeSpan _maxRetryDuration;

    public SqlRetryPolicy(SqlRetryOptions options, ILogger<SqlRetryPolicy> logger, TimeProvider timeProvider)
        : this(options, logger, timeProvider, ex => SqlFailures.IsTransient(ex, options.RetryTimeouts))
    {
    }

    /// <summary>For tests: decides what counts as transient without needing a real <c>SqlException</c>.</summary>
    internal SqlRetryPolicy(SqlRetryOptions options, ILogger logger, TimeProvider timeProvider, Func<Exception, bool> isTransient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(isTransient);
        options.Validate();

        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _maxRetryDuration = options.MaxRetryDuration;

        if (options.MaxRetries == 0)
        {
            _pipeline = ResiliencePipeline.Empty;
            return;
        }

        _pipeline = new ResiliencePipelineBuilder { TimeProvider = timeProvider }
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetries,
                Delay = options.BaseDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is { } ex
                    && !args.Context.CancellationToken.IsCancellationRequested
                    && isTransient(ex)
                    && WithinRetryDuration(args.Context)),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        RetryEvent,
                        args.Outcome.Exception,
                        "Transient database failure; retry {Retry} of {MaxRetries} in {DelayMs:0} ms.",
                        args.AttemptNumber + 1,
                        options.MaxRetries,
                        args.RetryDelay.TotalMilliseconds);
                    return default;
                },
            })
            .Build();
    }

    /// <summary>No retries: each operation runs once. Used where no policy is supplied (for example in tests).</summary>
    public static SqlRetryPolicy None { get; } =
        new(new SqlRetryOptions { MaxRetries = 0 }, NullLogger.Instance, TimeProvider.System, _ => false);

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(StartedAt, _time.GetTimestamp());
        try
        {
            return await _pipeline.ExecuteAsync(
                static async (ctx, op) => await op(ctx.CancellationToken),
                context,
                operation);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private bool WithinRetryDuration(ResilienceContext context)
        => !context.Properties.TryGetValue(StartedAt, out var started)
           || _time.GetElapsedTime(started) < _maxRetryDuration;
}
