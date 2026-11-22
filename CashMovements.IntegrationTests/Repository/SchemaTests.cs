using CashMovements.Infrastructure.Persistence;
using CashMovements.IntegrationTests.Support;
using Microsoft.Data.SqlClient;

namespace CashMovements.IntegrationTests.Repository;

/// <summary>
/// The schema script builds the ledger index as UNIQUE on a new database, and upgrades a database created before that
/// (with a non-unique index) when it's run again, as the development auto-setup does on every start.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SchemaTests
{
    private const string LedgerIndex = "SELECT is_unique FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Movement') AND name = N'CX_Movement_Ledger' AND type_desc = N'CLUSTERED';";

    private readonly SqlServerFixture _db;

    public SchemaTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task The_ledger_index_is_a_unique_clustered_index()
        => Assert.True(await ScalarAsync<bool>(LedgerIndex));

    [Fact]
    public async Task Rerunning_the_schema_script_upgrades_a_non_unique_ledger_index()
    {
        // Put the index back the way older databases have it.
        await _db.ExecuteAsync("""
            CREATE CLUSTERED INDEX CX_Movement_Ledger
                ON dbo.Movement (AccountId, Currency, OccurredAt, Id)
                WITH (DROP_EXISTING = ON);
            """);
        Assert.False(await ScalarAsync<bool>(LedgerIndex));
        var rows = await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Movement;");

        await DatabaseSetup.RunScriptsAsync(_db.ConnectionString, [Path.Combine(AppContext.BaseDirectory, "Database", "001_schema.sql")]);

        Assert.True(await ScalarAsync<bool>(LedgerIndex));
        Assert.Equal(rows, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Movement;"));
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
