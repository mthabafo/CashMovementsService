using System.Data;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;

namespace CashMovements.Infrastructure.Persistence;

/// <summary>
/// Decides which <see cref="SqlException"/>s mean "the database can't be reached right now" (worth retrying later)
/// rather than a bug. Those become <see cref="Domain.Exceptions.StorageUnavailableException"/>; everything else is left
/// alone and reported as an internal error.
/// </summary>
internal static class SqlFailures
{
    private const int CommandTimeout = -2;

    // Severity 20 and above: a fatal error that closes the connection (for example the server shutting down).
    private const byte FatalSeverity = 20;

    // Network and availability errors once connected: server not found or not accessible (53), no process at the
    // other end of the pipe (233), network name gone (64), semaphore timeout (121), connection aborted / reset /
    // timed out (10053, 10054, 10060), and Azure SQL's transient "busy / moving / unavailable" errors.
    private static readonly HashSet<int> ConnectionErrors =
    [
        53, 64, 121, 233, 10053, 10054, 10060,
        40197, 40501, 40613, 49918, 49919, 49920,
    ];

    /// <summary>
    /// True when <paramref name="ex"/> is a timeout or connection failure, either a <see cref="SqlException"/> or one
    /// that Dapper wrapped in a <see cref="DataException"/> because it happened while reading a row. A failure caused by
    /// the caller cancelling is never counted: that's a cancellation, not an outage.
    /// </summary>
    public static bool IsUnavailable(
        Exception ex,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out SqlException? sqlException)
    {
        sqlException = ex as SqlException ?? (ex as DataException)?.InnerException as SqlException;

        return sqlException is not null
               && !cancellationToken.IsCancellationRequested
               && (sqlException.Number == CommandTimeout
                   || sqlException.Class >= FatalSeverity
                   || ConnectionErrors.Contains(sqlException.Number));
    }
}
