using CashMovements.Domain.Exceptions;
using CashMovements.Domain.ValueObjects;
using CashMovements.Infrastructure.Persistence;
using CashMovements.Infrastructure.Repositories;
using CashMovements.IntegrationTests.Support;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Data.SqlClient;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.IntegrationTests.Repository;

/// <summary>
/// A row that breaks the domain's rules (here a zero amount, smuggled past the CHECK constraint) must fail loudly as a
/// server-side data problem: <see cref="CorruptMovementDataException"/> from the repository, <c>Internal</c> over gRPC
/// with no details. Never <c>InvalidArgument</c>, which would blame a caller whose input was fine.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CorruptDataTests : IAsyncLifetime
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _db;
    private readonly MovementRepository _repository;
    private readonly string _account = "C" + Guid.NewGuid().ToString("N");
    private long _corruptId;

    public CorruptDataTests(SqlServerFixture db)
    {
        _db = db;
        _repository = new MovementRepository(new SqlConnectionFactory(db.ConnectionString));
    }

    /// <summary>Inserts one zero-amount row with the amount constraint briefly switched off.</summary>
    public async Task InitializeAsync()
    {
        await _db.ExecuteAsync("ALTER TABLE dbo.Movement NOCHECK CONSTRAINT CK_Movement_Amount;");
        try
        {
            await _db.ExecuteAsync($"""
                INSERT INTO dbo.Movement (AccountId, ExternalRef, Amount, Currency, OccurredAt)
                VALUES (N'{_account}', N'BAD-1', 0, 'ZAR', '{OccurredAt:yyyy-MM-ddTHH:mm:ss}+00:00');
                """);
        }
        finally
        {
            // Switched back on immediately (not yet re-validated, since the bad row exists) so other inserts are checked.
            await _db.ExecuteAsync("ALTER TABLE dbo.Movement CHECK CONSTRAINT CK_Movement_Amount;");
        }

        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Id FROM dbo.Movement WHERE AccountId = @a AND ExternalRef = N'BAD-1';", connection);
        command.Parameters.AddWithValue("@a", _account);
        _corruptId = (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Removes the bad row and re-validates the constraint, so the database is trusted again.</summary>
    public async Task DisposeAsync()
    {
        await _db.ExecuteAsync($"DELETE FROM dbo.Movement WHERE AccountId = N'{_account}';");
        await _db.ExecuteAsync("ALTER TABLE dbo.Movement WITH CHECK CHECK CONSTRAINT CK_Movement_Amount;");
    }

    [Fact]
    public async Task Find_reports_the_corrupt_row_as_a_data_problem_not_a_domain_error()
    {
        var ex = await Assert.ThrowsAsync<CorruptMovementDataException>(
            () => _repository.FindByExternalRefAsync(AccountId.From(_account), ExternalRef.From("BAD-1"), default));

        Assert.Equal(_corruptId, ex.MovementId);
        Assert.IsType<InvalidMovementException>(ex.InnerException);
        Assert.IsNotAssignableFrom<DomainException>(ex);
    }

    [Fact]
    public async Task Statement_fails_with_the_data_problem_when_it_reaches_the_corrupt_row()
    {
        await using var statement = await _repository.OpenStatementAsync(
            AccountId.From(_account), Currency.From("ZAR"), OccurredAt.AddDays(-1), OccurredAt.AddDays(1), default);

        var ex = await Assert.ThrowsAsync<CorruptMovementDataException>(async () =>
        {
            await foreach (var _ in statement.Movements)
            {
            }
        });

        Assert.Equal(_corruptId, ex.MovementId);
    }

    [Fact]
    public async Task Over_grpc_a_corrupt_row_is_Internal_with_no_details()
    {
        using var host = new GrpcTestHost(_db.ConnectionString);

        using var call = host.Client.GetStatement(new Proto.GetStatementRequest
        {
            AccountId = _account,
            Currency = "ZAR",
            From = Timestamp.FromDateTimeOffset(OccurredAt.AddDays(-1)),
            To = Timestamp.FromDateTimeOffset(OccurredAt.AddDays(1)),
        });

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await foreach (var _ in call.ResponseStream.ReadAllAsync())
            {
            }
        });

        Assert.Equal(StatusCode.Internal, ex.StatusCode);
        Assert.DoesNotContain("zero", ex.Status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_corruptId.ToString(), ex.Status.Detail);
    }
}
