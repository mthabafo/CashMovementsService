# CashMovements

Design notes and trade-offs (such as how large statements are streamed) are in [SOLUTION.md](SOLUTION.md).

## Manual testing

- **Visual Studio:** pick the **Server + GRPC Client** launch profile and press F5.
- **Command line:** `.\test.ps1` from this folder.

Either way the local database is set up automatically. See [CashMovements.Grpc.Client/README.md](CashMovements.Grpc.Client/README.md).

## Legacy WCF gateway

A .NET Framework 4.8 WCF (NetTcp) service that returns an account's current balance by calling the gRPC service.

- **Visual Studio:** pick the **Server + WCF Client** launch profile and press F5.
- **Command line:** `.\test.ps1 -Legacy` opens a `legacy>` prompt (type e.g. `ACC-1001 ZAR`); `.\test.ps1 -Legacy ACC-2002 ZAR` gets one balance and exits.

Run instructions, ports and configuration are in [CashMovements.Wcf/README.md](CashMovements.Wcf/README.md).

| Port | Service |
|---|---|
| 5145 | gRPC (HTTP/2) |
| 5146 | gRPC-Web (HTTP/1.1), used by the WCF gateway |
| 8095 | WCF gateway (net.tcp) |

## Automated tests

```powershell
dotnet test CashMovements.slnx
```

Repository and end-to-end tests need SQL Server: they use the local default instance, or the server in
`CASHMOVEMENTS_TEST_SQLSERVER` (a connection string without a database). Each run creates and drops its own database.

### Volume test

`StatementVolumeTests` streams a 150,000-line statement (with 50,000 earlier movements behind the opening balance)
end to end through gRPC and SQL Server. It checks every line arrives in order with the right running balance, and that
the statement is streamed, not buffered: the first line arrives quickly and live memory stays flat. It takes about
6 seconds, most of it seeding, and runs with the rest of the tests.

```powershell
dotnet test CashMovements.IntegrationTests --filter Category=Volume --logger "console;verbosity=detailed"   # run it alone, with timings
dotnet test CashMovements.slnx --filter Category!=Volume                                                   # everything else
```

Measured on a developer machine: first line after ~20 ms, all 150,000 lines in ~1.8 s, live memory growth ~0.1 MB.
