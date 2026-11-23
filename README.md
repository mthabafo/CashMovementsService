# CashMovements

A service for an account's cash movements. A movement is one credit or debit in one currency. The service records
movements, returns the current balance per account and currency, and produces statements. A statement has an opening
balance, each movement in the period with a running balance, and a closing balance. It's returned as a stream or
exported as a CSV file. Recording is idempotent: sending the same movement again never counts it twice.

There are two entry points over the same data:

```text
gRPC client    ──gRPC, port 5145─────────────────────────────────────▶ CashMovements.Grpc ──▶ SQL Server
legacy client  ──net.tcp, port 8095──▶ CashMovements.Wcf ──gRPC-Web, port 5146──┘   (.NET 8)
                                        (.NET Framework 4.8)
```

- **CashMovements.Grpc** is the service, on .NET 8. All the rules and all database access are here.
- **CashMovements.Wcf** is a gateway for legacy .NET Framework clients. It returns balances only, and it gets them by
  calling the gRPC service, so the two entry points can't disagree.
- **CashMovements.Grpc.Client** and **CashMovements.Wcf.Client** are console clients for trying the service by hand.

The architecture, design decisions and trade-offs are in [SOLUTION.md](SOLUTION.md).

## API

The contract is [`CashMovements.Grpc/Protos/cash_movements.proto`](CashMovements.Grpc/Protos/cash_movements.proto)
(package `cashmovements.v1`, service `Movements`).

| Call | Does |
|---|---|
| `RecordMovement` | Records one movement and returns it, with its `id`. Sending the same account and `external_ref` again with the same amount and currency returns the original movement, and nothing is added |
| `GetBalance` | The sum of all of an account's movements in one currency, as at now |
| `GetStatement` | Server streaming: one header with the opening balance, then each movement in `[from, to)` with its running balance, in order, then one footer with the closing balance and line count |
| `ExportStatement` | Server streaming: the same statement as a CSV file. One start message with the file name and line count, then the file in 64 KB chunks, then one end message with the closing balance, byte count and SHA-256 |

The legacy WCF gateway has one operation, `GetBalance(accountId, currency)`, at
`net.tcp://localhost:8095/CashMovements/Balance`. Its contract is in [CashMovements.Wcf/README.md](CashMovements.Wcf/README.md).

- **Amounts are strings**, such as `"-250.75"`, so no precision is lost. They are signed: positive is a credit and
  negative is a debit. Responses always have exactly 2 decimal places.
- **Times are `google.protobuf.Timestamp`**, so UTC. A statement's `from` is included and its `to` is not.

### Errors

| gRPC status | When | Worth retrying? |
|---|---|---|
| `InvalidArgument` | A field is missing or breaks one of the [input rules](#input-rules). The message says which one | No: fix the request |
| `AlreadyExists` | The account already has that `external_ref` with a different amount or currency. The original movement is unchanged | No |
| `Unavailable` | The database couldn't be reached, even after retries | Yes, later |
| `Internal` | Anything unexpected. The details are in the server's log (event 2003) and are never sent to the caller | No |
| `Cancelled`, `DeadlineExceeded` | The caller cancelled, or its deadline passed | Up to the caller |

The WCF gateway returns a `BalanceFault` with the code `InvalidArgument`, `BackendUnavailable` or `Internal`.

If a streaming call fails part-way, the stream ends with an error status and no footer or end message. Discard what
arrived and ask again.

### Input rules

| Field | Rule |
|---|---|
| `account_id` | Required, at most 34 characters. Spaces at either end are trimmed. **Case-sensitive**: `ACC-1001` and `acc-1001` are different accounts. Any account ID is accepted: there's no list of accounts, so an account with no movements has a balance of `0.00` |
| `external_ref` | Required, at most 64 characters, trimmed, case-sensitive. Unique per account; the same reference can be used on different accounts |
| `amount` | Required. Not zero, at most 2 decimal places, and between -99999999999999999.99 and 99999999999999999.99 (what the database column holds). `.` as the decimal separator. No thousands separators, exponents or spaces, so `1,5`, `1e3` and `1 000` are rejected |
| `currency` | Required, 3 letters, such as `ZAR`. Lower case is accepted and stored in upper case. Any 3 letters pass: the service doesn't check the ISO 4217 list |
| `occurred_at` | Required. Not in the future; up to 5 minutes ahead is allowed for clock differences. Past dates are fine |
| `narration` | Optional, at most 140 characters, trimmed. Blank is stored as no narration |
| `from`, `to` | Both required, and `from` must be before `to` |

When the same `external_ref` is sent again, only the **amount and currency** decide whether it's a retry or a conflict.
If those match, the original movement is returned as it was, even if the date or narration differs.

## Manual testing in Visual Studio

### Before you start

- **Visual Studio 2022**, version 17.14 or later, so it opens `.slnx` solutions. It needs the **ASP.NET and web
  development** and **.NET desktop development** workloads, which include the .NET 8 SDK and the .NET Framework 4.8
  targeting pack.
- **SQL Server.** By default this is the local default instance (`Server=.`) with Windows authentication. To use
  another instance, see [The database connection string](#the-database-connection-string). The account you connect
  with must be able to create databases. Nothing else needs setting up. In Development, the gRPC server creates the
  `CashMovements` database on its first start and runs the scripts in this folder. Those scripts also seed the sample
  data.
- Open `CashMovements.slnx`. The launch profiles used below are in `CashMovements.slnLaunch` and show up in the
  startup dropdown on the toolbar, next to the green Start button. If they don't, close and reopen the solution.

### The database connection string

Only the gRPC server connects to SQL Server. The WCF gateway calls the gRPC server, so it has no connection string.

The server reads `ConnectionStrings:CashMovements`. For local development, it's in
`CashMovements.Grpc/appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "CashMovements": "Server=.;Database=CashMovements;Integrated Security=True;TrustServerCertificate=True"
  },
  "Database": {
    "AutoSetup": true
  }
}
```

In Visual Studio, it's under **CashMovements.Grpc** in Solution Explorer: expand `appsettings.json` to see
`appsettings.Development.json`. Change `Server=` to match your SQL Server, and keep `Database=CashMovements`, or use
any database name you like. The database doesn't have to exist, because `Database:AutoSetup` creates it.

| Your SQL Server | Value in `appsettings.Development.json` |
|---|---|
| Default instance on this machine | `Server=.;Database=CashMovements;Integrated Security=True;TrustServerCertificate=True` |
| Named instance, e.g. SQL Express | `Server=.\\SQLEXPRESS;Database=CashMovements;Integrated Security=True;TrustServerCertificate=True` |
| LocalDB, installed with Visual Studio | `Server=(localdb)\\MSSQLLocalDB;Database=CashMovements;Integrated Security=True;TrustServerCertificate=True` |
| SQL login instead of Windows authentication | `Server=.;Database=CashMovements;User ID=<user>;Password=<password>;TrustServerCertificate=True` |

- **Escape backslashes.** In JSON, a backslash is written `\\`, so `.\SQLEXPRESS` becomes `.\\SQLEXPRESS`.
- **Keep passwords out of the repository.** `appsettings.Development.json` is committed. To use a SQL login, or not to
  edit the file, set the environment variable `ConnectionStrings__CashMovements` instead (two underscores). It
  overrides the file. You can set it:
  - for every run, with `setx ConnectionStrings__CashMovements "<connection string>"`. Then restart Visual Studio so
    it picks it up;
  - or only for Visual Studio, in **CashMovements.Grpc > Properties > Debug > Open debug launch profiles UI >
    Environment variables**, on the `http` profile. That's saved in `Properties/launchSettings.json`, which is also
    committed, so don't commit a password there.
- **The automated tests use their own setting.** They read `CASHMOVEMENTS_TEST_SQLSERVER`, a connection string
  *without* a database, for example `Server=(localdb)\MSSQLLocalDB;Integrated Security=True;TrustServerCertificate=True`.
  It defaults to `Server=.`. Each test run creates and drops its own database, so it never touches `CashMovements`.
  Set it with `setx`, then restart Visual Studio so Test Explorer sees it.

If the server can't connect, it stops at startup with the SQL Server error in its window. Check `Server=` first.

Seed data, on a fresh database:

| Account | Currency | Balance | Notes |
|---|---|---|---|
| `ACC-1001` | ZAR | 39600.01 | 5 movements, October and November 2026 |
| `ACC-1001` | USD | 900.00 | 2 movements |
| `ACC-2002` | ZAR | -250.00 | Overdrawn |
| `ACC-VOLUME` | ZAR | -73408282.50 | 150,000 movements from 1 January to late July 2026, for large statements |

### 1. The gRPC service

1. In the startup dropdown, choose **Server + GRPC Client** and press **F5**.
2. Two console windows open:
   - **CashMovements.Grpc**, the server, shows its log. Wait for `Now listening on: http://localhost:5145`. The very
     first start takes a few seconds longer, because it seeds `ACC-VOLUME`.
   - **CashMovements.Grpc.Client** shows the `cashmovements.grpc>` prompt. Type into this window.
3. Try these commands. Replace `PAY-1` with a reference you haven't used before on this database.

   | Type | What you should see |
   |---|---|
   | `balance ACC-1001 ZAR` | `ACC-1001: 39600.01 ZAR` |
   | `record ACC-1001 PAY-1 -149.50 ZAR --narration "Groceries"` | The new movement, with its Id |
   | The same `record` again | The same movement and the same Id: a re-submission isn't counted twice |
   | `balance ACC-1001 ZAR` | `39450.51`, which is 149.50 less, once |
   | `record ACC-1001 PAY-1 -200 ZAR` | `AlreadyExists: External reference 'PAY-1' is already recorded with different details.` |
   | `record ACC-1001 PAY-2 1.005 ZAR` | `InvalidArgument: Amount must have at most 2 decimal places.` |
   | `record ACC-1001 PAY-2 0 ZAR` | `InvalidArgument: Amount must not be zero.` |
   | `balance ACC-1001 ZA` | `InvalidArgument: Currency 'ZA' is not a 3-letter ISO 4217 code.` |
   | `balance ACC-9999 ZAR` | `0.00`. There's no accounts table, so an account with no movements has a zero balance |
   | `statement ACC-1001 ZAR --from 2026-10-01 --to 2026-12-01` | Opening balance, one line per movement with a running balance, then the closing balance |
   | `export ACC-VOLUME ZAR --from 2026-01-01 --to 2026-12-01` | A progress bar, then the path of a 150,000-line CSV in the `exports` folder at the repository root |
   | `demo` | A scripted run on a fresh account: record, retry, conflicting reuse, invalid amount, balance, statement |

   Type `help` for all commands and options. For each record or statement, the server window logs an event,
   such as `MovementRecorded`, `MovementReplayed`, `MovementConflict` or `StatementCompleted`.
4. To finish, type `exit` in the client window, then stop debugging (**Shift+F5**) to stop the server.

**Server + GRPC Client demo** does the same, but runs `demo` and stops.

### 2. The legacy WCF gateway

A .NET Framework 4.8 WCF (NetTcp) service that returns an account's current balance. It has no business logic of its
own: it calls the gRPC service.

1. In the startup dropdown, choose **Server + WCF Client** and press **F5**.
2. Three console windows open:
   - **CashMovements.Grpc**, the gRPC server;
   - **CashMovements.Wcf**, the gateway, listening on `net.tcp://localhost:8095/CashMovements/Balance`;
   - **CashMovements.Wcf.Client**, the legacy client, which shows the `cashmovements.wcf>` prompt. Type into this
     window. If the gateway or server is still starting, the first call waits for it (up to 30 seconds).
3. Try these. Type `<account> <currency>`; `balance <account> <currency>` also works.

   | Type | What you should see |
   |---|---|
   | `ACC-1001 ZAR` | `Balance: 39600.01 ZAR`, the same as the gRPC client's `balance ACC-1001 ZAR` |
   | `ACC-1001 USD` | `Balance: 900.00 USD` |
   | `ACC-2002 ZAR` | `Balance: -250.00 ZAR` |
   | `ACC-1001 ZA` | `InvalidArgument: Currency 'ZA' is not a 3-letter ISO 4217 code.`, the gRPC service's own message |
   | `ACC-9999 ZAR` | `Balance: 0.00 ZAR` |

4. **Both entry points over the same data.** While this profile is running, right-click **CashMovements.Grpc.Client**
   in Solution Explorer and choose **Debug > Start New Instance**. At its `cashmovements.grpc>` prompt, record a
   movement, for example `record ACC-1001 PAY-3 100 ZAR`. Then ask the legacy client for `ACC-1001 ZAR` again: the
   balance is 100 higher, the same as the gRPC client's `balance ACC-1001 ZAR`.
5. **Backend down.** Close the **CashMovements.Grpc** window, then ask the legacy client for a balance again. After
   the gateway's 10-second backend timeout at most, it prints `BackendUnavailable: ...`. The gateway window logs
   `BackendUnavailable`. Press **Shift+F5** and then **F5** to start again.
6. **Follow one call across both services.** Each gateway log line has a `CorrelationId`. The gRPC server's log
   lines for that call carry the same ID.
7. To finish, type `exit` in the legacy client, then press **Shift+F5**.

All the processes in a profile run under the debugger, so you can set breakpoints anywhere, for example in
`MovementGrpcService` or `LegacyBalanceService`, and step through a call.

### Without Visual Studio

From this folder, `.\test.ps1` starts the server and opens the gRPC client. `.\test.ps1 -Legacy` does the same for
the WCF gateway and the legacy client, and `.\test.ps1 -Legacy ACC-2002 ZAR` gets one balance and exits. Typing
`exit` stops everything the script started.

More detail on each client is in [CashMovements.Grpc.Client/README.md](CashMovements.Grpc.Client/README.md) and
[CashMovements.Wcf/README.md](CashMovements.Wcf/README.md). That second README also covers the WCF gateway's
contract, faults and configuration.

| Port | Service |
|---|---|
| 5145 | gRPC (HTTP/2) |
| 5146 | gRPC-Web (HTTP/1.1), used by the WCF gateway |
| 8095 | WCF gateway (net.tcp) |

### Starting again with a fresh database

Recording movements changes the seed balances, and a reference that has been used once can't be used again with a
different amount. To start again from the seed data, stop the server and drop the database:

```powershell
sqlcmd -S . -E -Q "ALTER DATABASE [CashMovements] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [CashMovements];"
```

Use your own server instead of `.` if you changed the connection string. Without `sqlcmd`, you can also do it in
Visual Studio: open **View > SQL Server Object Explorer**, find the `CashMovements` database, choose **Delete**, and
tick **Close existing connections**.

The next time the server starts, it creates the database again and reseeds it, including the 150,000 `ACC-VOLUME`
movements. That takes a few seconds. Only drop the development database this way: nothing else is recreated.

## Troubleshooting

| Problem | Cause and fix |
|---|---|
| The build fails with *"Could not copy ... it is being used by another process"* | A server or client from an earlier run is still running and has its files locked. Close its console window, or press **Shift+F5**, and build again |
| The server stops at startup with *"address already in use"*, or the gateway with *"There is already a listener on IP endpoint ...:8095"* | Something else is using port 5145, 5146 or 8095. Usually it's an earlier copy still running. To find it, run `Get-Process -Id (Get-NetTCPConnection -LocalPort 5145).OwningProcess`, then close it |
| The server stops at startup with *"A network-related or instance-specific error occurred"* | It can't reach SQL Server. Check `Server=` in the [connection string](#the-database-connection-string), and that the SQL Server service is running. A named instance also needs the SQL Server Browser service |
| *"CREATE DATABASE permission denied"* or *"Login failed"* | The account in the connection string can't create the database or log in. Use an account that can, or create the empty `CashMovements` database yourself; the server then adds the tables and data |
| The launch profiles aren't in the startup dropdown, or a console window opens and closes straight away | Visual Studio hasn't loaded `CashMovements.slnLaunch`, or still has the old one. Close and reopen the solution. Multi-project launch profiles need Visual Studio 2022 17.11 or later |
| The gRPC client prints *"Unavailable"*, or can't connect | The server isn't running, or hasn't finished starting. Look at its window for errors, and check it says `Now listening on: http://localhost:5145` |
| The legacy client prints *"Could not reach the gateway"* | CashMovements.Wcf isn't running. Start it with the **Server + WCF Client** profile |
| The legacy client prints *"BackendUnavailable"* | The gateway is running but the gRPC server isn't, or the server can't reach the database. Check the gRPC server's window |
| A statement has no lines | The period has no movements. The seed accounts have movements in October and November 2026, and `ACC-VOLUME` from January to July 2026. Remember that the `--to` date isn't included |
| PowerShell won't run `test.ps1` | Scripts are blocked by the execution policy. Run `powershell -ExecutionPolicy Bypass -File .\test.ps1` instead |
| `test.ps1` seems to run old code | If a server is already listening on port 5145, `test.ps1` uses it as it is. Stop that server first, so the script builds and starts the current code |

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

With the server running (`.\test.ps1`, or the **Server + GRPC Client** launch profile), at the `cashmovements.grpc>`
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
