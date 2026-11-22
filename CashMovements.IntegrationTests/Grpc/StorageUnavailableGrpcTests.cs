using CashMovements.IntegrationTests.Support;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.IntegrationTests.Grpc;

/// <summary>
/// When the database can't be reached, every call reports UNAVAILABLE (temporary, worth retrying) with no internal
/// details, never INTERNAL (a bug). The WCF gateway turns UNAVAILABLE into its BackendUnavailable fault
/// (LegacyBalanceServiceTests), so legacy clients are told to retry too.
/// Needs no SQL Server: the host points at a port where nothing is listening.
/// </summary>
public sealed class StorageUnavailableGrpcTests : IDisposable
{
    private const string UnreachableDatabase =
        "Server=tcp:127.0.0.1,1;Database=CashMovements;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=2";

    private const string ExpectedDetail = "The service is temporarily unavailable. Try again later.";

    private readonly GrpcTestHost _host = new(UnreachableDatabase);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task GetBalance_is_unavailable()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await _host.Client.GetBalanceAsync(new Proto.GetBalanceRequest { AccountId = "ACC1", Currency = "ZAR" }));

        AssertUnavailable(ex);
    }

    [Fact]
    public async Task RecordMovement_is_unavailable()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await _host.Client.RecordMovementAsync(new Proto.RecordMovementRequest
            {
                AccountId = "ACC1",
                ExternalRef = "REF-1",
                Amount = "10",
                Currency = "ZAR",
                OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-1)),
            }));

        AssertUnavailable(ex);
    }

    [Fact]
    public async Task GetStatement_is_unavailable_before_any_message_is_sent()
    {
        using var call = _host.Client.GetStatement(new Proto.GetStatementRequest
        {
            AccountId = "ACC1",
            Currency = "ZAR",
            From = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-30)),
            To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var ex = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext());

        AssertUnavailable(ex);
    }

    private static void AssertUnavailable(RpcException ex)
    {
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.Equal(ExpectedDetail, ex.Status.Detail); // nothing about SQL Server, servers or ports
    }
}
