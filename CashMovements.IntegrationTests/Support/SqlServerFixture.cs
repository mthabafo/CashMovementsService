using Microsoft.Data.SqlClient;

namespace CashMovements.IntegrationTests.Support;

/// <summary>
/// A fresh, uniquely named database on an existing SQL Server, built from 001_schema.sql and 002_procedures.sql,
/// shared by every test in the <see cref="SqlServerCollection"/> and dropped afterwards. Tests stay independent by
/// using their own (random) account IDs.
/// <para>
/// The server defaults to the local default instance (<see cref="DefaultServer"/>). To use another one, set
/// <see cref="ServerVariable"/> to a connection string without a database.
/// </para>
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string ServerVariable = "CASHMOVEMENTS_TEST_SQLSERVER";
    public const string DefaultServer = "Server=.;Integrated Security=True;TrustServerCertificate=True";

    private static readonly string[] Scripts = ["001_schema.sql", "002_procedures.sql"];

    private readonly string _databaseName = $"CashMovementsTests_{Guid.NewGuid():N}";
    private string _serverConnectionString = "";

    /// <summary>Connection string for the test database.</summary>
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ServerVariable);
        _serverConnectionString = string.IsNullOrWhiteSpace(configured) ? DefaultServer : configured;

        try
        {
            await ExecuteAsync(_serverConnectionString, $"CREATE DATABASE [{_databaseName}];");
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                $"Could not create the test database. Make sure SQL Server is running, or set {ServerVariable} " +
                "to a connection string (without a database) for a server you can create databases on.", ex);
        }

        ConnectionString = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = _databaseName }.ConnectionString;

        foreach (var script in Scripts)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Database", script);
            foreach (var batch in SplitOnGo(await File.ReadAllTextAsync(path)))
                await ExecuteAsync(ConnectionString, batch);
        }
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();

        await ExecuteAsync(_serverConnectionString,
            $"IF DB_ID(N'{_databaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]; END;");
    }

    /// <summary>Runs ad-hoc SQL against the test database (for arranging bulk data).</summary>
    public Task ExecuteAsync(string sql) => ExecuteAsync(ConnectionString, sql);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Splits a script into batches on lines containing only GO, as sqlcmd and SSMS do.</summary>
    private static IEnumerable<string> SplitOnGo(string script)
    {
        var batch = new System.Text.StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (batch.ToString().Trim().Length > 0)
                    yield return batch.ToString();
                batch.Clear();
            }
            else
            {
                batch.AppendLine(line.TrimEnd('\r'));
            }
        }

        if (batch.ToString().Trim().Length > 0)
            yield return batch.ToString();
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
