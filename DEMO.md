# CashMovements – video demo script

About 9 minutes: a live demo, the architecture, the key decisions and trade-offs, and a few interesting implementations.

## Before recording (5 minutes)

- Start the server once beforehand. Its first start on the developer database switches on `READ_COMMITTED_SNAPSHOT`,
  rebuilds the ledger index and seeds `ACC-VOLUME` with 150,000 movements, which takes a few seconds you don't want on
  camera.
- Run `dotnet test CashMovements.slnx` beforehand (about 30 seconds) and keep the result on screen for the end.
- Make the console and editor fonts larger, close unrelated windows, and have [SOLUTION.md](SOLUTION.md) and Solution
  Explorer ready.
- Record in short segments (demo, architecture, decisions) and join them; it's easier than one perfect take.

## 1. Intro (0:00–0:30)

> "This is CashMovements: a ledger service that records cash movements, returns balances and streams statements over
> gRPC, with a .NET Framework WCF gateway for legacy clients. I'll demo it, walk through the architecture, then cover the
> key decisions and trade-offs."

## 2. Live demo (0:30–3:30)

Start the **Server + GRPC Client** launch profile in Visual Studio. Point out the server console: the database is set up
automatically, and the logs are structured.

At the `cashmovements.grpc>` prompt:

```text
balance ACC-1001 ZAR
record ACC-1001 DEMO-1 -149.50 ZAR --narration "Groceries"
record ACC-1001 DEMO-1 -149.50 ZAR          ← same Id back: a retry, not a second movement
record ACC-1001 DEMO-1 -999 ZAR             ← AlreadyExists: same reference, different amount
record ACC-1001 DEMO-2 1.005 ZAR            ← InvalidArgument: more than two decimal places
statement ACC-VOLUME ZAR --from 2026-01-01 --to 2026-01-01T01:00
export ACC-VOLUME ZAR --from 2026-01-01 --to 2026-03-12
```

- During the export, point at the progress bar (about 50,000 lines in under a second, checksum verified), then open the
  file in the `exports` folder to show the Opening, Movement and Closing rows.
- Switch to the **Server + WCF Client** profile and type `ACC-1001 ZAR` at the `legacy>` prompt:
  > "The same balance through the old WCF contract. The gateway has no logic of its own; it calls the same gRPC
  > service."
- Show the same `CorrelationId` in the gateway's log and the server's log.

## 3. Architecture (3:30–5:30)

Walk through Solution Explorer:

- **Layers:** Domain → Application → Infrastructure → gRPC API.
  > "Dependencies point inward, and architecture tests enforce it, so a violation fails the build."
- [Movement.cs](CashMovements.Domain/Entities/Movement.cs): the rules live in the domain; value objects reject bad
  input when they're built.
- [MovementService.cs](CashMovements.Application/MovementService.cs), `RecordMovementAsync`: idempotency, and
  identical requests that arrive at the same moment.
- [MovementRepository.cs](CashMovements.Infrastructure/Repositories/MovementRepository.cs): Dapper over stored
  procedures, a snapshot statement streamed without buffering, and the retry policy.
- [cash_movements.proto](CashMovements.Grpc/Protos/cash_movements.proto): the streamed statement (header, lines,
  footer) and the export stream.
- `CashMovements.Wcf`: the NetTcp gateway calling gRPC-Web, because .NET Framework can't do HTTP/2 without TLS.

## 4. Design decisions and trade-offs (5:30–7:30)

Keep [SOLUTION.md](SOLUTION.md) on screen and pick about five:

- **Streaming large statements:** constant memory and the first line within milliseconds; the trade-off is that a
  connection and snapshot stay open for the whole stream.
- **Idempotency through a unique key:** optimistic concurrency, with no locks taken in advance; a request that loses
  the insert race is treated as a retry. A conflict is `AlreadyExists`, because silently returning the original would
  mislead the caller.
- **Snapshot reads and `READ_COMMITTED_SNAPSHOT`:** reads and writes never block each other, and single-row inserts
  keep deadlocks unlikely.
- **Clustered index in ledger order:** a balance or statement is one range read; the trade-off is slightly costlier
  inserts.
- **Retries, then `Unavailable`:** transient database failures are retried, and clients are told "try again", not "bug".
- **Simplifications:** for example no authentication and no account registry, each with what production would do
  instead.

## 5. Interesting implementations (7:30–9:00)

- **Tests that prove themselves:** the simultaneous-requests test, the volume test and the export test each failed
  against a deliberately broken version of the code.
  > "A test is only worth something if it fails when the code is wrong."
- **The export:** the line count comes from the same snapshot as the lines, the client verifies a SHA-256, and the
  `.partial` file is renamed only when the export is intact.
- **The end-to-end parity test:** it starts the real .NET 8 server as a process from the .NET Framework tests and
  compares both entry points over the same data.

## 6. Close (9:00–9:30)

Show the saved test run (about 240 tests passing):

> "That's CashMovements: streamed, idempotent, and consistent across both entry points. The design notes are in
> SOLUTION.md."

## Tips

- Talk about *why* more than *what*: the code is on screen, the reasoning isn't.
- If a command misbehaves while recording, keep going; one recovered mistake looks natural.
