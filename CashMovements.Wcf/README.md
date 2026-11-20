# CashMovements.Wcf — legacy gateway

A minimal WCF (NetTcp) service on .NET Framework 4.8 for legacy clients. It exposes one operation, the current
balance of an account, and calls into the same backend as everything else: the CashMovements gRPC service.

```
legacy client ──net.tcp:8095──▶ CashMovements.Wcf ──gRPC-Web, http:5146──▶ CashMovements.Grpc ──▶ SQL Server
 (.NET Framework)                 (.NET Framework 4.8)                       (.NET 8)
```

The gateway holds no business logic. Validation and balance rules stay in the backend, so the two APIs can't drift.
.NET Framework can't use HTTP/2 without TLS, so the gateway calls the backend with gRPC-Web over HTTP/1.1; the
gRPC service accepts that on its own port (5146) next to the normal gRPC port (5145).

## Contract

```csharp
[ServiceContract(Namespace = "urn:cashmovements:legacy:v1")]
interface ILegacyBalanceService
{
    [OperationContract, FaultContract(typeof(BalanceFault))]
    Task<BalanceResult> GetBalanceAsync(string accountId, string currency);   // WCF operation name: GetBalance
}
```

- `BalanceResult`: `AccountId`, `Currency`, `Amount` (decimal, signed), `AsAtUtc`.
- Failures are `FaultException<BalanceFault>` with `Code`:

  | Code | When | Retry? |
  |---|---|---|
  | `InvalidArgument` | Bad account ID or currency (the backend's message is passed on) | No |
  | `BackendUnavailable` | The gRPC service is down or didn't answer within the timeout | Later |
  | `Internal` | Anything else. Details are logged by the gateway, never returned | — |

The contract source is in `Contracts/`. CashMovements.Wcf.Client compiles those files directly; clients that generate
proxies can use the metadata endpoint `net.tcp://localhost:8095/CashMovements/Balance/mex`.

## Running it

The gateway needs the gRPC service running. Easiest, from the solution folder:

```powershell
.\test.ps1 -Legacy
```

This builds and starts the gRPC service and the gateway, then opens the legacy client's prompt:

```text
legacy> ACC-1001 ZAR
  Account: ACC-1001
  Balance: 39600.01 ZAR
  As at:   2026-11-20 08:15:02Z

legacy> exit
```

Typing `exit` stops everything the script started. To get one balance without the prompt, pass it:
`.\test.ps1 -Legacy ACC-2002 ZAR`.

In Visual Studio, pick the **Server + WCF Client** launch profile and press F5. Three consoles open
(gRPC server, gateway, legacy client); type into the legacy client's `legacy>` prompt. It waits for the other two to
finish starting before the first call. If a window opens and closes straight away after the launch profiles changed,
Visual Studio is still using the old ones: close and reopen the solution.

Or start each piece yourself, in separate terminals:

```powershell
dotnet run --project CashMovements.Grpc --launch-profile http
.\CashMovements.Wcf\bin\Debug\net48\CashMovements.Wcf.exe
.\CashMovements.Wcf.Client\bin\Debug\net48\CashMovements.Wcf.Client.exe
```

(Build first with `dotnet build CashMovements.slnx`.) The gateway runs until Ctrl+C. The client takes
`<account> <currency>` at its prompt, or as arguments for a single call (`... CashMovements.Wcf.Client.exe ACC-1001 ZAR`).

## Configuration

`CashMovements.Wcf\App.config` (copied to `CashMovements.Wcf.exe.config`):

| Setting | Default | Meaning |
|---|---|---|
| `appSettings/GrpcBackendUrl` | `http://localhost:5146` | The gRPC service's gRPC-Web endpoint (`Kestrel:Endpoints:GrpcWeb` in its `appsettings.json`) |
| `appSettings/BackendTimeoutSeconds` | `10` | Deadline for each backend call |
| service base address | `net.tcp://localhost:8095/CashMovements/Balance` | Where the gateway listens |
| `netTcpBinding` "WindowsTransport" | Transport security, Windows credentials, encrypt and sign | Callers authenticate with their Windows account; nothing to configure locally |

The client's endpoint is in `CashMovements.Wcf.Client\App.config` and must match the gateway's address and binding;
`--address <net.tcp url>` overrides the address for one call.

- **No admin rights needed.** A NetTcp listener doesn't need a URL reservation (unlike WCF over HTTP), and the
  gateway doesn't use the Net.Tcp Port Sharing service.
- **Runs alongside the gRPC host.** Different process and ports (gRPC 5145/5146, WCF 8095).
- **Windows only.** WCF hosting needs .NET Framework.

## Tests

`CashMovements.Wcf.Tests` (run by `dotnet test CashMovements.slnx`): result mapping and every error → fault mapping
with a fake backend, plus real NetTcp round trips through `ServiceHost` and `ChannelFactory` with the gateway's
binding. The gRPC-Web side of the backend is tested in `CashMovements.IntegrationTests`.
