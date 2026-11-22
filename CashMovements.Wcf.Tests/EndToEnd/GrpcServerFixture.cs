using System.Data.Common;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Proto = CashMovements.Grpc.Contracts;

namespace CashMovements.Wcf.Tests.EndToEnd;

/// <summary>
/// Runs the real CashMovements.Grpc server (.NET 8, so in its own process: it can't share a process with these .NET
/// Framework tests) against a throwaway SQL Server database that its own development auto-setup creates.
/// The server is the build output of CashMovements.Grpc in the same configuration, so build the solution first
/// (<c>dotnet test CashMovements.slnx</c> and Visual Studio both do). SQL Server: the local default instance, or the
/// server in <c>CASHMOVEMENTS_TEST_SQLSERVER</c>, as for the other database tests.
/// </summary>
public sealed class GrpcServerFixture : IAsyncLifetime
{
    private const string ServerVariable = "CASHMOVEMENTS_TEST_SQLSERVER";
    private const string DefaultServer = "Server=.;Integrated Security=True;TrustServerCertificate=True";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    private readonly string _databaseName = "CashMovementsParity_" + Guid.NewGuid().ToString("N");
    private readonly StringBuilder _output = new();
    private string _serverConnectionString = "";
    private Process? _process;
    private GrpcChannel? _channel;

    /// <summary>The server's gRPC-Web endpoint (HTTP/1.1), the one the WCF gateway calls.</summary>
    public Uri GrpcWebAddress { get; private set; } = null!;

    /// <summary>A gRPC client for the server, over gRPC-Web: .NET Framework can't use HTTP/2 without TLS.</summary>
    public Proto.Movements.MovementsClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ServerVariable);
        _serverConnectionString = string.IsNullOrWhiteSpace(configured) ? DefaultServer : configured!;
        var databaseConnectionString = new DbConnectionStringBuilder { ConnectionString = _serverConnectionString };
        databaseConnectionString["Database"] = _databaseName;

        GrpcWebAddress = new Uri($"http://127.0.0.1:{Support.GatewayHost.FreePort()}");
        var serverDll = FindServer();

        var start = new ProcessStartInfo("dotnet", $"\"{serverDll}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(serverDll)!, // content root: appsettings.json
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "ParityTests"; // not Development: no developer database
        start.EnvironmentVariables["ConnectionStrings__CashMovements"] = databaseConnectionString.ConnectionString;
        start.EnvironmentVariables["Database__AutoSetup"] = "true"; // create the database and run the scripts
        start.EnvironmentVariables["Kestrel__Endpoints__Grpc__Url"] = $"http://127.0.0.1:{Support.GatewayHost.FreePort()}";
        start.EnvironmentVariables["Kestrel__Endpoints__GrpcWeb__Url"] = GrpcWebAddress.ToString().TrimEnd('/');
        start.EnvironmentVariables["Logging__LogLevel__Default"] = "Warning";

        _process = new Process { StartInfo = start };
        _process.OutputDataReceived += (_, e) => Capture(e.Data);
        _process.ErrorDataReceived += (_, e) => Capture(e.Data);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _channel = GrpcChannel.ForAddress(GrpcWebAddress, new GrpcChannelOptions
        {
            HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler()) { HttpVersion = HttpVersion.Version11 },
        });
        Client = new Proto.Movements.MovementsClient(_channel);

        await WaitUntilReadyAsync();
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();

        if (_process is not null)
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(10_000);
            }

            _process.Dispose();
        }

        await DropDatabaseAsync();
    }

    private async Task WaitUntilReadyAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (_process!.HasExited)
                throw new InvalidOperationException($"The gRPC server exited during startup (code {_process.ExitCode}).\n{Output()}");

            try
            {
                await Client.GetBalanceAsync(
                    new Proto.GetBalanceRequest { AccountId = "READY-CHECK", Currency = "ZAR" },
                    deadline: DateTime.UtcNow.AddSeconds(5));
                return;
            }
            catch (RpcException) when (stopwatch.Elapsed < StartupTimeout)
            {
                await Task.Delay(250);
            }
            catch (RpcException ex)
            {
                throw new InvalidOperationException($"The gRPC server didn't become ready within {StartupTimeout.TotalSeconds:0} s: {ex.Status}.\n{Output()}");
            }
        }
    }

    private async Task DropDatabaseAsync()
    {
        var master = new DbConnectionStringBuilder { ConnectionString = _serverConnectionString };
        master["Database"] = "master";

        SqlConnection.ClearAllPools();
        using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(
            $"IF DB_ID(N'{_databaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]; END;",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>CashMovements.Grpc's build output, in the same configuration (Debug/Release) as these tests.</summary>
    private static string FindServer()
    {
        var testOutput = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)); // ...\bin\Debug\net48
        var configuration = testOutput.Parent!.Name;

        var root = testOutput;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CashMovements.slnx")))
            root = root.Parent;
        if (root is null)
            throw new InvalidOperationException("Couldn't find the solution folder (CashMovements.slnx) above " + testOutput.FullName);

        var serverDll = Path.Combine(root.FullName, "CashMovements.Grpc", "bin", configuration, "net8.0", "CashMovements.Grpc.dll");
        if (!File.Exists(serverDll))
            throw new InvalidOperationException($"The gRPC server isn't built ({serverDll}). Build the solution first: dotnet build CashMovements.slnx");

        return serverDll;
    }

    private void Capture(string? line)
    {
        if (line is null)
            return;
        lock (_output)
            _output.AppendLine(line);
    }

    private string Output()
    {
        lock (_output)
            return _output.ToString();
    }
}
