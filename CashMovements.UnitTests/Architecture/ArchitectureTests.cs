using System.Reflection;
using CashMovements.Application;
using CashMovements.Domain.Entities;
using CashMovements.Grpc.Services;
using CashMovements.Infrastructure.Repositories;
using NetArchTest.Rules;

namespace CashMovements.UnitTests.Architecture;

/// <summary>
/// Enforces the dependency rule: API → Application → Domain ← Infrastructure.
/// Dependencies point inward; the Domain depends on nothing; Infrastructure plugs in by implementing the Domain's
/// interfaces; the API reaches Infrastructure only in its composition root (Program).
/// <para>
/// Agreed trade-off: the API may use Domain types that pass through the Application layer (for example
/// <c>Movement</c> and the statement read models), so there are no rules against API → Domain.
/// </para>
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(Movement).Assembly;
    private static readonly Assembly Application = typeof(MovementService).Assembly;
    private static readonly Assembly Infrastructure = typeof(MovementRepository).Assembly;
    private static readonly Assembly Api = typeof(MovementGrpcService).Assembly;

    // Libraries that belong to the outer layers.
    private const string Dapper = "Dapper";
    private const string SqlClient = "Microsoft.Data.SqlClient";
    private const string GrpcLibrary = "Grpc";
    private const string Protobuf = "Google.Protobuf";
    private const string AspNetCore = "Microsoft.AspNetCore";

    [Fact]
    public void Domain_depends_on_no_other_layer()
        => AssertNoDependency(Types.InAssembly(Domain), "CashMovements.Application", "CashMovements.Infrastructure", "CashMovements.Grpc");

    [Fact]
    public void Domain_depends_on_no_framework_or_data_access_library()
        => AssertNoDependency(Types.InAssembly(Domain), Dapper, SqlClient, GrpcLibrary, Protobuf, AspNetCore, "Microsoft.Extensions");

    [Fact]
    public void Application_depends_only_on_the_domain()
        => AssertNoDependency(Types.InAssembly(Application), "CashMovements.Infrastructure", "CashMovements.Grpc");

    [Fact]
    public void Application_depends_on_no_data_access_or_transport_library()
        => AssertNoDependency(Types.InAssembly(Application), Dapper, SqlClient, GrpcLibrary, Protobuf, AspNetCore);

    [Fact]
    public void Infrastructure_depends_only_on_the_domain()
        => AssertNoDependency(Types.InAssembly(Infrastructure), "CashMovements.Application", "CashMovements.Grpc");

    [Fact]
    public void Infrastructure_depends_on_no_transport_library()
        => AssertNoDependency(Types.InAssembly(Infrastructure), GrpcLibrary, Protobuf, AspNetCore);

    [Fact]
    public void Api_uses_infrastructure_only_in_its_composition_root()
        // Program (top-level statements) lives in the global namespace; every other API type is under CashMovements.Grpc.
        => AssertNoDependency(
            Types.InAssembly(Api).That().ResideInNamespace("CashMovements.Grpc"),
            "CashMovements.Infrastructure", Dapper, SqlClient);

    [Fact]
    public void Api_composition_root_is_the_only_type_outside_the_api_namespace()
    {
        // Guards the rule above: a new class added to the global namespace would otherwise escape it.
        var outside = Types.InAssembly(Api)
            .That().DoNotResideInNamespace("CashMovements.Grpc")
            .GetTypes()
            .Select(t => t.FullName ?? t.Name)
            .Where(name => !name.StartsWith("Program", StringComparison.Ordinal)            // Program and its nested closures
                        && !name.StartsWith("<", StringComparison.Ordinal)                  // compiler-generated
                        && !name.StartsWith("Microsoft.", StringComparison.Ordinal)         // SDK-generated (e.g. Razor/IL metadata)
                        && !name.StartsWith("System.", StringComparison.Ordinal))
            .ToList();

        Assert.True(outside.Count == 0, "Only Program may live outside the CashMovements.Grpc namespace. Found: " + string.Join(", ", outside));
    }

    private static void AssertNoDependency(PredicateList types, params string[] forbidden)
        => Report(types.ShouldNot().HaveDependencyOnAny(forbidden).GetResult(), forbidden);

    private static void AssertNoDependency(Types types, params string[] forbidden)
        => Report(types.ShouldNot().HaveDependencyOnAny(forbidden).GetResult(), forbidden);

    private static void Report(TestResult result, string[] forbidden)
        => Assert.True(
            result.IsSuccessful,
            $"These types depend on {string.Join(" / ", forbidden)}: {string.Join(", ", result.FailingTypeNames ?? [])}");
}
