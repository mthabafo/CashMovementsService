# CashMovements.Client

A command-line client for manually testing the CashMovements gRPC service. It only knows the service through
`cash_movements.proto` (no reference to the server project), like any external client.

## 1. Create the local database (once)

The server's Development settings use the database `CashMovements` on the local default SQL Server instance
(`Server=.`, Windows authentication). From the solution folder:

```powershell
sqlcmd -S . -E -Q "IF DB_ID('CashMovements') IS NULL CREATE DATABASE CashMovements"
sqlcmd -S . -E -d CashMovements -b -i 001_schema.sql
sqlcmd -S . -E -d CashMovements -b -i 002_procedures.sql
sqlcmd -S . -E -d CashMovements -b -i 003_seed.sql
```

All three scripts can be re-run safely. The seed adds accounts `ACC-1001` (ZAR and USD) and `ACC-2002` (ZAR, overdrawn).

## 2. Start the server

```powershell
dotnet run --project CashMovements.Grpc --launch-profile http
```

It listens on `http://localhost:5145`.

## 3. Run commands

In another terminal, from the solution folder:

```powershell
dotnet run --project CashMovements.Client -- balance ACC-1001 ZAR
dotnet run --project CashMovements.Client -- record ACC-1001 PAY-77 -149.50 ZAR --narration "Groceries"
dotnet run --project CashMovements.Client -- statement ACC-1001 ZAR --from 2026-10-01 --to 2026-12-01
dotnet run --project CashMovements.Client -- demo
```

| Command | Calls |
|---|---|
| `balance <account> <currency>` | `GetBalance` |
| `record <account> <ref> <amount> <currency> [--at <time>] [--narration <text>]` | `RecordMovement`; `--at` defaults to now |
| `statement <account> <currency> [--from <time>] [--to <time>]` | `GetStatement`; defaults to the 30 days before `--to` (default now) |
| `demo [<account>]` | A scripted check on a fresh account: record, retry, conflicting reuse, invalid amount, balance, statement |

- Every command takes `--server <url>` to use another address. `--help` shows usage.
- Amounts are sent exactly as typed, so invalid input (`1,5`, `1.005`, `0`) can be tried to see the server reject it.
- Times without an offset are local time; a bare date means midnight. Output times are UTC.
- Server errors are printed as `<StatusCode>: <detail>`, for example `AlreadyExists: ...`.
- Exit codes: `0` success, `1` server error (or an unexpected demo result), `2` bad command line.
