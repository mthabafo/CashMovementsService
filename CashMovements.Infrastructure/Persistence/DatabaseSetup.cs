using System.Text;
using Microsoft.Data.SqlClient;

namespace CashMovements.Infrastructure.Persistence;

/// <summary>
/// Creates a database and runs the repo's SQL scripts against it. Used for local development (the server sets the
/// database up on startup when <c>Database:AutoSetup</c> is true) and by the integration tests. Not a migration tool:
/// it relies on every script being idempotent.
/// </summary>
public static class DatabaseSetup
{
    /// <summary>Creates the database named in <paramref name="connectionString"/> if it doesn't exist.</summary>
    /// <returns>True if the database was created, false if it already existed.</returns>
    public static async Task<bool> EnsureDatabaseExistsAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var target = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(target.InitialCatalog))
            throw new ArgumentException("The connection string must name a database.", nameof(connectionString));

        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };

        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // CREATE DATABASE can't take a parameter, so the name is quoted with QUOTENAME inside dynamic SQL.
        await using var command = new SqlCommand(
            """
            IF DB_ID(@name) IS NOT NULL
                SELECT CAST(0 AS BIT);
            ELSE
            BEGIN
                DECLARE @sql NVARCHAR(400) = N'CREATE DATABASE ' + QUOTENAME(@name);
                EXEC (@sql);
                SELECT CAST(1 AS BIT);
            END
            """,
            connection);
        command.Parameters.AddWithValue("@name", target.InitialCatalog);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Runs each script, in the order given, batch by batch (batches are separated by GO lines).</summary>
    public static async Task RunScriptsAsync(
        string connectionString,
        IEnumerable<string> scriptPaths,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var path in scriptPaths)
        {
            var script = await File.ReadAllTextAsync(path, cancellationToken);
            foreach (var batch in SplitOnGo(script))
            {
                await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    /// <summary>Splits a script into batches on lines containing only GO, as sqlcmd and SSMS do.</summary>
    public static IEnumerable<string> SplitOnGo(string script)
    {
        var batch = new StringBuilder();
        foreach (var rawLine in script.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (batch.ToString().Trim().Length > 0)
                    yield return batch.ToString();
                batch.Clear();
            }
            else
            {
                batch.AppendLine(line);
            }
        }

        if (batch.ToString().Trim().Length > 0)
            yield return batch.ToString();
    }
}
