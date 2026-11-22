namespace CashMovements.Infrastructure.Persistence;

/// <summary>Retry settings for transient database failures, bound from <c>Database:Retry</c>.</summary>
public sealed class SqlRetryOptions
{
    public const string SectionName = "Database:Retry";

    /// <summary>Retries after the first attempt. 0 turns retrying off.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Delay before the first retry; it doubles for each retry after that, with random jitter.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>No new retry starts once this much time has passed since the first attempt.</summary>
    public TimeSpan MaxRetryDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Also retry command timeouts. Off by default: a timeout usually means the database is overloaded or blocked, and
    /// retrying adds load and multiplies the caller's wait.
    /// </summary>
    public bool RetryTimeouts { get; set; }

    internal void Validate()
    {
        if (MaxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRetries), MaxRetries, "Must be 0 or more.");
        if (BaseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(BaseDelay), BaseDelay, "Must not be negative.");
        if (MaxRetryDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxRetryDuration), MaxRetryDuration, "Must be positive.");
    }
}
