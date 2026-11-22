using CashMovements.Domain.Exceptions;
using Microsoft.Data.SqlClient;

namespace CashMovements.Infrastructure.Persistence;

/// <summary>Opens connections to the CashMovements database. Registered as a singleton.</summary>
public interface ISqlConnectionFactory
{
    /// <summary>Returns an open connection. The caller owns it and must dispose it.</summary>
    /// <exception cref="StorageUnavailableException">The database can't be reached.</exception>
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
            // Any failure to connect (server down or unreachable, database offline, login rejected) means the store
            // can't be used right now. The details stay in the inner exception, for the server log.
            await connection.DisposeAsync();
            throw new StorageUnavailableException(ex);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
