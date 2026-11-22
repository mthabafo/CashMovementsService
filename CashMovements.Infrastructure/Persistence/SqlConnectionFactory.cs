using Microsoft.Data.SqlClient;

namespace CashMovements.Infrastructure.Persistence;

/// <summary>Opens connections to the CashMovements database. Registered as a singleton.</summary>
public interface ISqlConnectionFactory
{
    /// <summary>Returns an open connection. The caller owns it and must dispose it.</summary>
    /// <remarks>Throws an exception that <c>SqlFailures</c> recognises as a failure to connect if the database can't be reached.</remarks>
    Task<SqlConnection> OpenAsync(CancellationToken cancellationToken);
}

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A connection string is required.", nameof(connectionString));

        _connectionString = connectionString;
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (SqlException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Marked as a connection failure, so it's retried and, if retries run out, reported as unavailable.
            await connection.DisposeAsync();
            throw new SqlConnectFailedException(ex);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
