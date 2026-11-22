using System.Data;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;

namespace CashMovements.Infrastructure.Persistence;

/// <summary>
/// Sorts SQL Server failures into two groups:
/// <list type="bullet">
/// <item><see cref="IsTransient"/>: worth retrying straight away (<see cref="SqlRetryPolicy"/>).</item>
/// <item><see cref="IsUnavailable"/>: what's left once retries run out is reported as
/// <see cref="Domain.Exceptions.StorageUnavailableException"/> (the API says UNAVAILABLE).</item>
/// </list>
/// Anything in neither group is left alone and reported as an internal error.
/// </summary>
internal static class SqlFailures
{
    private const int CommandTimeout = -2;
    private const int Deadlock = 1205;

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
    /// True when retrying may succeed: the connection couldn't be opened or was lost, or the command was chosen as a
    /// deadlock victim. A command timeout only when <paramref name="retryTimeouts"/> is set: a timeout usually means the
    /// database is overloaded or blocked, and repeating the query adds load and multiplies the caller's wait.
    /// </summary>
    public static bool IsTransient(Exception ex, bool retryTimeouts)
    {
        if (ex is SqlConnectFailedException)
            return true;

        var sql = Unwrap(ex);
        return sql is not null
               && (IsConnectionLost(sql) || sql.Number == Deadlock || (retryTimeouts && sql.Number == CommandTimeout));
    }

    /// <summary>
    /// True when <paramref name="ex"/> means the store can't be used right now: a failure to connect, a lost connection,
    /// a deadlock or a timeout. A failure caused by the caller cancelling is never counted: that's a cancellation, not an
    /// outage.
    /// </summary>
    public static bool IsUnavailable(
        Exception ex,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out SqlException? sqlException)
    {
        sqlException = Unwrap(ex);

        return sqlException is not null
               && !cancellationToken.IsCancellationRequested
               && (ex is SqlConnectFailedException
                   || IsConnectionLost(sqlException)
                   || sqlException.Number is Deadlock or CommandTimeout);
    }

    private static bool IsConnectionLost(SqlException sql)
        => sql.Class >= FatalSeverity || ConnectionErrors.Contains(sql.Number);

    // Dapper wraps a failure that happens while reading a row in a DataException.
    private static SqlException? Unwrap(Exception ex) => ex switch
    {
        SqlException sql => sql,
        SqlConnectFailedException { InnerException: SqlException sql } => sql,
        DataException { InnerException: SqlException sql } => sql,
        _ => null,
    };
}

/// <summary>
/// Opening a connection failed (server down or unreachable, database offline, login rejected). Kept apart from errors
/// on an open connection because every failure to connect is worth retrying, whatever its error number.
/// </summary>
internal sealed class SqlConnectFailedException(SqlException inner)
    : Exception("Could not open a connection to the database.", inner);
