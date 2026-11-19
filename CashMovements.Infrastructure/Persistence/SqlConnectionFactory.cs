using Microsoft.Data.SqlClient;

namespace CashMovements.Infrastructure.Persistence;

/// <summary>Opens connections to the CashMovements database. Registered as a singleton.</summary>
public interface ISqlConnectionFactory
{
    /// <summary>Returns an open connection. The caller owns it and must dispose it.</summary>
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
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
