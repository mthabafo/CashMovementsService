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

## Database failures and retries

Transient database failures (a failed or dropped connection, a failover, a deadlock) are retried up to 3 times, with a
delay that starts at 200 ms and doubles each time (with jitter), and no new retry starts after 5 seconds. Retrying stops
as soon as the caller cancels or its deadline passes. If retries run out, the call fails with `Unavailable` (the WCF
gateway returns `BackendUnavailable`). Command timeouts aren't retried by default, and a statement that has started
streaming isn't retried: the client asks again. Settings are under `Database:Retry` in `appsettings.json`.

For retries to help, set these in each environment's connection string:

| Setting | Suggested | Why |
|---|---|---|
| `Connect Timeout` | `2` to `5` | Fail fast when the server is down, so several attempts fit in the 5-second retry window (default 15 s) |
| `Pool Blocking Period` | `NeverBlock` | Otherwise, after a failed connection, SqlClient fails new attempts straight away for 5 s or more, so retries can't succeed |
| `ConnectRetryCount` / `ConnectRetryInterval` | `3` / `2` | SqlClient quietly re-establishes broken idle pooled connections, for example after a failover |

## Large statements (50,000+ lines) and export

In development, `ACC-VOLUME` has 150,000 ZAR movements, one every 2 minutes from 1 January to late July 2026 (about
720 a day). It's seeded by `004_seed_volume.sql`, which only the development auto-setup runs, on the server's first
start.

With the server running (`.	est.ps1`, or the **Server + GRPC Client** launch profile), at the `cashmovements.grpc>`
prompt:

```text
statement ACC-VOLUME ZAR --from 2026-01-01 --to 2026-03-12      # about 50,400 lines, printed to the console
export    ACC-VOLUME ZAR --from 2026-01-01 --to 2026-03-12      # the same lines saved as CSV, with a progress bar
export    ACC-VOLUME ZAR --from 2026-01-01 --to 2026-12-01      # all 150,000 lines
```

Or as a one-off command, while the server is running:

```powershell
dotnet run --project CashMovements.Grpc.Client -- statement ACC-VOLUME ZAR --from 2026-01-01 --to 2026-03-12
```

```powershell
dotnet run --project CashMovements.Grpc.Client -- export ACC-VOLUME ZAR --from 2026-01-01 --to 2026-03-12
```

- Each day adds about 720 lines, so adjust `--to` for a different size. Times without an offset are local time.
- Printing 50,000 lines to the console takes a while; `export` is the quicker way to get them all.
- Exports are saved in the `exports` folder at the repository root (created if needed, ignored by git), or wherever
  `--out` points.

`ExportStatement` streams the statement as a CSV file: a start message with the file name and line count, the file in
64 KB chunks, and an end message with the closing balance, byte count and SHA-256. The client keeps the file only if it
checks out.

## Logging

Both services write structured logs to the console: readable single lines in development, and JSON (one object per
line, with scopes) elsewhere. The gRPC server switches by environment (`Logging:Console` in `appsettings.json`); the
WCF gateway uses `LogFormat` in its `App.config`.

- **Correlation:** every log written during a gRPC call carries `GrpcMethod` and `CorrelationId`. The ID comes from the
  caller's `x-correlation-id` header, or is generated, and is returned in the response trailers. The WCF gateway
  generates one per call and sends it on, so a legacy call can be followed into the server's logs.
- **Privacy:** account IDs are masked to their last 4 characters (`****1001`); amounts, narrations and connection
  strings are never logged.

| Event ID | Name | Level | Logged when |
|---|---|---|---|
| 1001 | `MovementRecorded` | Information | A new movement is recorded |
| 1002 | `MovementReplayed` | Information | A re-submission returns the original movement (`LostInsertRace` if it arrived at the same moment) |
| 1003 | `MovementConflict` | Warning | A re-used reference has a different amount or currency (`ConflictingFields`) |
| 1101 | `StatementCompleted` | Information | A statement finished (`LineCount`, `ElapsedMs`) |
| 1102 | `StatementEndedEarly` | Warning | A statement was cancelled, failed or abandoned part-way (`Reason`) |
| 2001 | `GrpcCallCompleted` | Information, Warning for server-side failures | Every gRPC call ends (`StatusCode`, `ElapsedMs`) |
| 2002 | `StorageUnavailable` | Warning | The database is unavailable after retries |
| 2003 | `UnhandledError` | Error | An unexpected error, reported to the caller as `Internal` |
| 2101 | `StatementExported` | Information | A statement export finished (`LineCount`, `ByteCount`, `ElapsedMs`) |
| 3001 | `DatabaseRetry` | Warning | A transient database failure is retried |
| 4001–4004 | `BalanceReturned`, `BalanceRejected`, `BackendUnavailable`, `BalanceFailed` | Information to Error | The WCF gateway's outcome for each call |

## Automated tests

```powershell
dotnet test CashMovements.slnx
```

Repository and end-to-end tests need SQL Server: they use the local default instance, or the server in
`CASHMOVEMENTS_TEST_SQLSERVER` (a connection string without a database). Each run creates and drops its own database.

`ParityTests` (in `CashMovements.Wcf.Tests`, `Category=EndToEnd`) checks the legacy and new entry points agree over the
same data, with nothing faked: it starts the built gRPC server in its own process on a throwaway database, records
movements through gRPC, and compares balances and validation errors from the WCF gateway (over net.tcp) with gRPC's.

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
