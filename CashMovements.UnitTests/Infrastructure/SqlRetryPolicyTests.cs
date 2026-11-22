using CashMovements.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashMovements.UnitTests.Infrastructure;

/// <summary>
/// The retry rules, without a database: a stand-in exception plays the transient SQL failure (a real SqlException can't
/// be constructed), delays are zero, and a manual clock drives the retry window.
/// </summary>
public class SqlRetryPolicyTests
{
    private sealed class TransientFailure : Exception;

    private readonly ManualClock _clock = new();
    private readonly ListLogger _logger = new();

    private SqlRetryPolicy Policy(int maxRetries = 3, double maxRetrySeconds = 5)
        => new(
            new SqlRetryOptions
            {
                MaxRetries = maxRetries,
                BaseDelay = TimeSpan.Zero,
                MaxRetryDuration = TimeSpan.FromSeconds(maxRetrySeconds),
            },
            _logger,
            _clock,
            ex => ex is TransientFailure);

    [Fact]
    public async Task Recovers_when_a_retry_succeeds_and_logs_each_retry()
    {
        var attempts = 0;

        var result = await Policy().ExecuteAsync(_ => ++attempts < 3 ? throw new TransientFailure() : Task.FromResult(42), default);

        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, _logger.Warnings);
    }

    [Fact]
    public async Task Gives_up_after_the_maximum_retries_and_rethrows_the_last_failure()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<TransientFailure>(
            () => Policy(maxRetries: 3).ExecuteAsync<int>(_ => { attempts++; throw new TransientFailure(); }, default));

        Assert.Equal(4, attempts); // the first attempt and 3 retries
    }

    [Fact]
    public async Task Never_retries_a_failure_that_is_not_transient()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Policy().ExecuteAsync<int>(_ => { attempts++; throw new InvalidOperationException(); }, default));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Stops_retrying_as_soon_as_the_caller_cancels()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<Exception>(() => Policy().ExecuteAsync<int>(_ =>
        {
            attempts++;
            cancellation.Cancel(); // e.g. the gRPC deadline passed during this attempt
            throw new TransientFailure();
        }, cancellation.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Starts_no_new_retry_once_the_retry_window_has_passed()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<TransientFailure>(() => Policy(maxRetries: 10, maxRetrySeconds: 5).ExecuteAsync<int>(_ =>
        {
            attempts++;
            _clock.Advance(TimeSpan.FromSeconds(3)); // each attempt takes 3 seconds
            throw new TransientFailure();
        }, default));

        Assert.Equal(2, attempts); // 3 s in: retry; 6 s in: past the 5-second window, so stop
    }

    [Fact]
    public async Task Zero_retries_runs_the_operation_once()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<TransientFailure>(
            () => Policy(maxRetries: 0).ExecuteAsync<int>(_ => { attempts++; throw new TransientFailure(); }, default));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task The_production_rules_do_not_retry_ordinary_exceptions()
    {
        var policy = new SqlRetryPolicy(
            new SqlRetryOptions { BaseDelay = TimeSpan.Zero },
            NullLogger<SqlRetryPolicy>.Instance,
            TimeProvider.System);
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => policy.ExecuteAsync<int>(_ => { attempts++; throw new InvalidOperationException(); }, default));

        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(-1, 0.2, 5)]
    [InlineData(3, -0.2, 5)]
    [InlineData(3, 0.2, 0)]
    public void Invalid_settings_are_rejected(int maxRetries, double baseDelaySeconds, double maxRetrySeconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SqlRetryPolicy(
            new SqlRetryOptions
            {
                MaxRetries = maxRetries,
                BaseDelay = TimeSpan.FromSeconds(baseDelaySeconds),
                MaxRetryDuration = TimeSpan.FromSeconds(maxRetrySeconds),
            },
            NullLogger<SqlRetryPolicy>.Instance,
            TimeProvider.System));

    /// <summary>A clock that only moves when told to.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;
    }

    private sealed class ListLogger : ILogger
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings++;
        }
    }
}
