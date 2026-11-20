# CashMovements.Grpc.Client

A command-line client for manually testing the CashMovements gRPC service. It only knows the service through
`cash_movements.proto` (no reference to the server project), like any external client.

## Quick start

Nothing needs setting up first. In Development the server creates the local database (`CashMovements` on
`Server=.`) if it's missing and runs `001_schema.sql`, `002_procedures.sql` and `003_seed.sql` on every start;
all three are safe to re-run. This is controlled by `Database:AutoSetup` in `appsettings.Development.json`.

**Visual Studio:** choose the **Server + GRPC Client** launch profile in the toolbar's startup dropdown and press F5.
The server and the interactive client start together. **Server + GRPC Client demo** runs the demo instead.
(The profiles are in `CashMovements.slnLaunch`.)

**Command line**, from the solution folder:

```powershell
.\test.ps1
```

This builds, starts the server, opens the interactive client, and stops the server when you type `exit`.
Pass a command to run just that and exit, for example `.\test.ps1 demo` or `.\test.ps1 balance ACC-1001 ZAR`.
Server output goes to a log file whose path is printed at start. If a server is already running on port 5145,
the script uses it and leaves it running. `.\test.ps1 -Legacy` does the same for the WCF gateway
(see [CashMovements.Wcf/README.md](../CashMovements.Wcf/README.md)).

If PowerShell refuses to run scripts, use `powershell -ExecutionPolicy Bypass -File .\test.ps1`.

## Commands

At the `cashmovements.grpc>` prompt (or as arguments for a one-off run):

| Command | Calls |
|---|---|
| `balance <account> <currency>` | `GetBalance` |
| `record <account> <ref> <amount> <currency> [--at <time>] [--narration <text>]` | `RecordMovement`; `--at` defaults to now |
| `statement <account> <currency> [--from <time>] [--to <time>]` | `GetStatement`; defaults to the 30 days before `--to` (default now) |
| `demo [<account>]` | A scripted check on a fresh account: record, retry, conflicting reuse, invalid amount, balance, statement |
| `help`, `clear`, `exit` | Prompt only |

```text
cashmovements.grpc> balance ACC-1001 ZAR
cashmovements.grpc> record ACC-1001 PAY-77 -149.50 ZAR --narration "Groceries"
cashmovements.grpc> statement ACC-1001 ZAR --from 2026-10-01 --to 2026-12-01
```

- The seed data has accounts `ACC-1001` (ZAR and USD) and `ACC-2002` (ZAR, overdrawn).
- Amounts are sent exactly as typed, so invalid input (`1,5`, `1.005`, `0`) can be tried to see the server reject it.
- Times without an offset are local time; a bare date means midnight. Output times are UTC.
- Server errors are printed as `<StatusCode>: <detail>`, for example `AlreadyExists: ...`.
- Ctrl+C cancels the running command; at an empty prompt it closes the client.
- The client connects to `http://localhost:5145` by default; start it with `--server <url>` to use another address.
- One-off runs exit with `0` on success, `1` on a server error (or an unexpected demo result), `2` on a bad command line.

## Running the pieces separately

```powershell
dotnet run --project CashMovements.Grpc --launch-profile http
dotnet run --project CashMovements.Grpc.Client
```
