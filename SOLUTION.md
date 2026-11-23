# CashMovements – solution notes

How the service meets its requirements, the main design decisions and their trade-offs, and what was simplified.

**Requirements**

- [Architecture](#architecture)
- [Balance math: money and time](#balance-math-money-and-time)
- [Uniqueness and idempotent re-submissions](#uniqueness-and-idempotent-re-submissions)
- [Field conflicts](#field-conflicts)
- [Concurrent submissions](#concurrent-submissions)
- [Race conditions](#race-conditions)
- [Large statements (50,000+ rows): streaming](#large-statements-50000-rows-streaming)
- [Validation](#validation)
- [Consistent outcomes across the new and legacy entry points](#consistent-outcomes-across-the-new-and-legacy-entry-points)
- [Testing](#testing)
- [Simplifications](#simplifications)

**Design details**

- [Technology choices](#technology-choices)
- [Optimistic concurrency control](#optimistic-concurrency-control)
- [Transactions, ACID and deadlocks](#transactions-acid-and-deadlocks)
- [Database failures: retries and Unavailable](#database-failures-retries-and-unavailable)
- [Indexes](#indexes)

**Extensions**

- [Statement export](#statement-export)
- [Structured logging](#structured-logging)

## Architecture

```text
CashMovements/
│
├── CashMovements.Domain/                   Business rules; no dependencies
│   ├── Entities/
│   │   └── Movement.cs                     A credit or debit; validates itself
│   ├── ValueObjects/
│   │   ├── AccountId.cs
│   │   ├── Currency.cs
│   │   └── ExternalRef.cs
│   ├── ReadModels/
│   │   ├── Balance.cs
│   │   ├── StatementHeader.cs
│   │   ├── StatementLine.cs
│   │   ├── StatementCalculator.cs          Running balance, one line at a time
│   │   └── StatementSource.cs
│   ├── Interfaces/
│   │   └── IMovementRepository.cs
│   └── Exceptions/                         Invalid input, duplicate, storage unavailable
│
├── CashMovements.Application/              Use cases
│   ├── Interfaces/
│   │   └── IMovementService.cs
│   ├── MovementService.cs                  Idempotent recording, balances, statements
│   ├── StatementStream.cs
│   └── Logging/
│       └── ApplicationLog.cs               Business-event logs
│
├── CashMovements.Infrastructure/           The only layer that knows about SQL Server
│   ├── Repositories/
│   │   └── MovementRepository.cs           Dapper over stored procedures
│   └── Persistence/
│       ├── SqlConnectionFactory.cs
│       ├── SqlRetryPolicy.cs               Retries with Polly
│       ├── SqlFailures.cs                  Which SQL errors are transient
│       └── DatabaseSetup.cs                Development auto-setup
│
├── CashMovements.Grpc/                     gRPC API (.NET 8)
│   ├── Protos/
│   │   └── cash_movements.proto            The contract
│   ├── Services/
│   │   └── MovementGrpcService.cs
│   ├── Mapping/
│   │   └── MovementMapper.cs               Proto ↔ domain types
│   ├── Interceptors/                       Errors → status codes, call logging
│   ├── Export/
│   │   └── StatementCsvExport.cs
│   └── Program.cs                          Composition root
│
├── CashMovements.Wcf/                      Legacy WCF gateway (.NET Framework 4.8)
│   ├── Contracts/                          ILegacyBalanceService, BalanceResult, BalanceFault
│   ├── LegacyBalanceService.cs
│   └── BalanceBackend.cs                   Calls the gRPC API over gRPC-Web
│
├── Database scripts (repository root)
│   ├── 001_schema.sql                      Table, constraints, indexes
│   ├── 002_procedures.sql                  Stored procedures
│   ├── 003_seed.sql                        Sample accounts
│   └── 004_seed_volume.sql                 150,000 movements (development only)
│
├── Tests
│   ├── CashMovements.UnitTests/            Domain, application, mapping, architecture
│   ├── CashMovements.IntegrationTests/     SQL Server, gRPC end to end, volume
│   └── CashMovements.Wcf.Tests/            Gateway, net.tcp, parity with gRPC
│
├── Tools
│   ├── CashMovements.Grpc.Client/          Console client for the gRPC API
│   ├── CashMovements.Wcf.Client/           Console client for the WCF gateway
│   └── test.ps1                            Builds, starts and opens a client
│
├── README.md
└── SOLUTION.md
```

How a request flows:

```text
gRPC client   ──HTTP/2───▶ CashMovements.Grpc ──▶ Application ──▶ Infrastructure ──▶ SQL Server
legacy client ──net.tcp──▶ CashMovements.Wcf  ──gRPC-Web──▶ CashMovements.Grpc
```

- **Dependencies point inward:** API → Application → Domain ← Infrastructure. Architecture tests enforce this. The
  API refers to Infrastructure only in `Program.cs`, to register it.
- **Rules live in the Domain**, the Application orchestrates them, and Infrastructure is the only layer that knows about
  SQL Server.
- **One table:** balances and statements are calculated from the single `Movement` table.
- **No logic in the legacy gateway:** it forwards to the same gRPC service, so both entry points share one
  implementation.

## Balance math: money and time

- **Exact money:** amounts are `decimal` and `DECIMAL(19,2)`, never floating point, and strings on the wire. Nothing
  is rounded: more than two decimal places is rejected.
- **Signed, per currency:** credits are positive and debits negative. Each currency has its own balance, with no
  conversion.
- **Balance:** the sum of the account's movements in that currency.
- **Statement for `[from, to)`:**
  - the opening balance is everything before `from`;
  - each line adds to a running balance, in date order, with `Id` breaking ties;
  - the closing balance is opening + lines.

  All three come from one snapshot.
- **Time:** stored with its offset and sent as UTC. `from` is included and `to` isn't, so back-to-back statements never
  miss or repeat a movement.
- **Trade-off:** statements are calculated, not stored. A movement recorded late with an earlier date changes
  statements already issued. Closing periods would prevent that.
- **Tested:** unit tests and SQL Server tests check the calculation, and the volume test checks every running balance
  over 150,000 lines.

## Uniqueness and idempotent re-submissions

`(AccountId, ExternalRef)` is the idempotency key: unique per account, not globally, and case-sensitive.
Before inserting, the service looks the pair up: a re-submission with the same amount and currency is a retry and gets
the original movement back without inserting, while a different amount or currency is rejected as `AlreadyExists`.
A unique constraint in SQL Server (binary collation) guarantees there's never a second row, whatever the application
does, so nothing can be double-counted.
If identical requests arrive at the same moment, the ones that lose the insert race catch the unique-key error, look
the pair up again and are treated as retries, so every caller gets the original movement back.

## Field conflicts

A repeated `externalRef` for the same account whose amount or currency differs from the original is rejected with
`AlreadyExists` and nothing is written, so it can never be double-counted; the same rule applies when the two requests
arrive at the same moment.
I chose an error over the alternatives because the caller is waiting and can fix it: silently returning the original
would let them believe their different amount was recorded, accepting it or parking it for review suits batch imports
rather than a live API, and a success with a "conflict" flag is easy to ignore.
`AlreadyExists` is the gRPC code for "what you tried to create already exists"; `InvalidArgument` would be wrong because
the request is valid on its own, and `Aborted` because retrying won't help.
Only amount and currency are compared, the fields that change a balance (formatting doesn't matter: `100` equals
`100.00`, `zar` equals `ZAR`); a re-submission with a different date or narration is treated as a retry and returns the
original movement, so the caller can see from the response which values were actually recorded.

## Concurrent submissions

Correctness never depends on timing: every decision that matters is made atomically by SQL Server, and the services hold
no state or in-memory locks, so any number of calls can run at once.
Different movements never wait on each other, because each is a single-row insert and balances are calculated rather
than stored, so there's no shared row to contend for or lose an update on; identical submissions get the original
movement, and conflicting ones get `AlreadyExists`.
Reads use row versions (`READ_COMMITTED_SNAPSHOT`, `SNAPSHOT` for statements), so balances and statements never block
or are blocked by inserts.
Tests cover simultaneous identical requests over gRPC and SQL Server and a mixed workload of writers and readers with
no retries to hide deadlocks; the limits are that it's tested at this scale only, and a very busy account's inserts
compete for the same index pages (a throughput limit, not a correctness one).

## Race conditions

Four races are handled, each covered by a test against SQL Server.
Two identical submissions at the same moment: the unique key lets one insert through, and the other is treated as a
retry, so both callers get the same movement.
A retry after a lost reply: if the first attempt was saved but its reply never arrived, the retry hits the unique key
and gets the original back, so nothing is counted twice.
Inserts during a statement: the statement reads one snapshot, so its opening balance, lines and export line count always
agree; and reads during writes: snapshot reads mean balances and lookups never wait on or block an insert.

## Large statements (50,000+ rows): streaming

Each statement row flows one at a time: SQL Server reads in index order with no sort, Dapper hands each row on
unbuffered, the service keeps only the running balance, and gRPC sends one message per line as it's produced.
The trade-offs: one connection and snapshot transaction stay open for the whole stream (that's what keeps the balances
consistent), the opening-balance sum gets slower as the account's history grows, and backpressure from a slow client
over a real network relies on HTTP/2 by design and isn't tested.

## Validation

Validation is layered, with each rule written once in the domain and inherited by both the gRPC and WCF APIs.
The API layer only parses (culture-invariant amounts, required timestamps); value objects reject bad account IDs,
references and currencies when they're built. `Movement` always enforces a non-zero amount with at most two decimal
places that fits the `DECIMAL(19,2)` column, whether created or read back. The future-date rule applies only when
recording.
The service treats a reused reference with the same amount and currency as a retry and a different one as a duplicate;
the database repeats the critical rules (CHECK constraints, `DECIMAL(19,2)`, a case-sensitive unique key) as a last
line of defence.
Bad input becomes `InvalidArgument` and duplicates `AlreadyExists`, but a corrupt stored row is reported as `Internal`
and logged, because the caller isn't at fault.

## Consistent outcomes across the new and legacy entry points

The legacy WCF gateway has no business logic or database access of its own: it forwards to the same gRPC `GetBalance`,
so both entry points share one implementation of validation, the balance query and retries, and can't drift apart.
Outcomes map one to one: the same balance value (a string over gRPC, a decimal over WCF), `InvalidArgument` with the same
message, `Unavailable` as a `BackendUnavailable` fault, and anything unexpected as a generic `Internal`; the server's
5-second retry window fits inside the gateway's 10-second deadline.
An end-to-end parity test proves it with nothing faked: it starts the real gRPC server on a throwaway database, records
movements, and checks the WCF gateway over net.tcp and gRPC return the same balances and the same validation errors.
The limits: the legacy side offers only balances, and the test calls gRPC over gRPC-Web, because .NET Framework can't
use HTTP/2 without TLS (other tests show gRPC-Web and HTTP/2 give the same results).

## Testing

There are about 250 test cases in three projects, and `dotnet test CashMovements.slnx` runs them all:

- **Unit tests** (`CashMovements.UnitTests`):
  - the domain rules: value objects, `Movement`, and the statement calculation;
  - `MovementService`'s idempotency and race handling, with a fake repository;
  - mapping to and from the gRPC types, the CSV export, the retry policy, and the business-event logs;
  - architecture tests, which use NetArchTest to fail the build when a layer depends on one it shouldn't.
- **Integration tests** (`CashMovements.IntegrationTests`), against a real SQL Server, each run on its own throwaway
  database:
  - the repository and schema: uniqueness, case sensitivity, snapshots;
  - simultaneous identical submissions, and a mixed load of writers and readers;
  - retries and outages, including killing the session mid-statement;
  - the gRPC service end to end, in memory: status codes, streaming, the export, logs and correlation IDs;
  - the client's export downloader;
  - a volume test: 150,000 lines streamed with flat memory.
- **WCF tests** (`CashMovements.Wcf.Tests`):
  - the gateway's mapping of each outcome to a result or fault, with a fake backend;
  - real net.tcp round trips;
  - the parity test, which starts the real gRPC server as a separate process and compares both entry points over the
    same data.

Tests use a real SQL Server rather than an in-memory fake. The behaviours that matter most (the unique key, snapshot
isolation, connection failures) are exactly the ones a fake wouldn't reproduce.

Each new test for a bug was checked against deliberately broken code, to prove it fails without the fix. That
included:
- removing the race handling;
- mapping outages to `Internal`;
- turning off `READ_COMMITTED_SNAPSHOT`;
- buffering the export;
- logging an unmasked account ID;
- making the two entry points disagree.

What isn't tested:
- production-scale load;
- a slow client over a real network, which relies on HTTP/2 flow control;
- a SQL Server failover;
- the console clients' interactive prompts, which were checked by hand.

## Simplifications

Deliberate simplifications, each with what a production system would do instead.

**Domain**
- **Accounts aren't modelled.** Any non-blank account ID of up to 34 characters is accepted, and an account with no
  movements simply has a zero balance rather than `NotFound`. A real system would check against an account registry,
  including whether the account may hold that currency.
- **Currencies are checked for shape only.** Any three letters pass (`XYZ` included), and every currency allows two
  decimal places, although ISO 4217 has currencies with none (JPY) or three (BHD). A real system would validate against
  the ISO list and each currency's minor units.
- **Movements are immutable.** There's no update, delete or reversal operation; a correction is recorded as a new,
  opposite movement.
- **References are unique forever.** An idempotency key never expires, and only amount and currency are compared when
  one is re-used (see Field conflicts).
- **Future dates allow 5 minutes of clock drift**, and statement periods have no maximum length.

**API and security**
- **The gRPC API has no TLS or authentication**: it listens on plain HTTP on localhost, and any caller can record
  movements on any account. Production would need TLS, authenticated callers and per-account authorisation. (The WCF
  gateway does use Windows authentication over net.tcp.)
- **The legacy gateway offers only `GetBalance`** and runs as a console app; production would host it as a Windows
  Service.
- **Conflict errors don't name the conflicting fields**; the message says only that the details differ.

**Data**
- **Balances are calculated on every read**, so a balance gets slower as an account's history grows (see Indexes).
- **Schema changes are idempotent SQL scripts** run by the server at startup in development, not a migration tool, and
  there's no versioning of which scripts have run. Production would apply them as a deployment step (for example with
  DbUp or a DACPAC); turning on `READ_COMMITTED_SNAPSHOT` and rebuilding the ledger index both need that.
- **One SQL Server database**, with no read replicas, partitioning or archiving of old movements.

**Operations**
- **Secrets:** development connects with Windows authentication, so no password is stored. Production would supply
  the connection string through environment variables or a vault, using a least-privilege login that can only run the
  stored procedures; none of that is set up here.
- **Logs go to the console only**: they're structured (JSON outside development, with event IDs and correlation IDs),
  but there's no central log store, metrics or distributed tracing; production would ship them to a log platform and
  add OpenTelemetry metrics and traces.
- **No circuit breaker or rate limiting**: transient database failures are retried, but a long outage is retried by
  every request.

**Testing**
- **Tests use a local SQL Server**, or the one in `CASHMOVEMENTS_TEST_SQLSERVER`, rather than containers, so a machine
  without SQL Server can run only the unit tests.
- **Load is tested at this scale only**: concurrency and volume tests run on one machine, not under production traffic.

## Technology choices

- **gRPC for the new API.** It gives a typed contract in one `.proto` file that clients generate code from, and server
  streaming, so a statement of any length is sent line by line rather than in pages. The legacy gateway reaches it
  over gRPC-Web, because .NET Framework can't use HTTP/2 without TLS.
- **Dapper rather than EF Core.**
  - The service runs a handful of fixed queries.
  - Statements must stream rows without buffering them or tracking changes.
  - Dapper keeps the exact SQL visible.

  EF Core pays off with a rich model and many queries. Here it would be a layer that generates SQL I'd want to control
  anyway, for little gain.
- **Stored procedures.**
  - Every access path is fixed and lives in one script (`002_procedures.sql`), next to the indexes it relies on, so the
    two are tuned together.
  - A production login could be limited to running them; that's not set up here.
- **Polly for retries.** Polly's backoff, jitter and time limits are well tested, so there's no hand-written retry loop.
- **Few design patterns, on purpose.**
  - Used:
    - a repository interface (`IMovementRepository`), so the Domain and Application layers don't know about SQL;
    - value objects, which validate themselves;
    - gRPC interceptors, which turn exceptions into status codes and log each call.
  - Not used: MediatR, CQRS, a unit of work or a generic repository. With one service, four operations and
    single-statement writes, they would add indirection without removing any complexity.

## Optimistic concurrency control

Concurrency control is optimistic: nothing is locked in advance, so a submission looks up its reference and inserts
without holding a lock in between, and the rare conflict is detected by the unique key at insert time and resolved as a
retry or `AlreadyExists`; reads use row versioning rather than shared locks.
The only locks are the brief exclusive ones SQL Server takes on a new row within a single `INSERT`.
This fits because conflicts are rare (almost always a client retry), pessimistic range locks would make every request
queue and risk deadlocks, and an append-only ledger with calculated balances has no lost updates to prevent.
There's no `rowversion` column because nothing is ever updated; if editing were added, an
`UPDATE ... WHERE RowVersion = @Expected` check would be the natural fit.

## Transactions, ACID and deadlocks

There's only one explicit transaction, because every write is a single `INSERT`, which SQL Server already runs as its
own atomic, durable transaction; balances are calculated, never stored, so there's never a second write to keep in step.
The exception is statements, which read the opening balance and the lines in one `SNAPSHOT` transaction so both come
from the same moment.
The "look up the ref, then insert" check isn't wrapped in a transaction: stopping that race would take serializable
range locks, which block and deadlock, so the unique constraint provides the guarantee instead.
Deadlocks are unlikely by design: each write locks only its own row for one statement, nothing updates or deletes,
every read uses row versions instead of shared locks (`SNAPSHOT` for statements, `READ_COMMITTED_SNAPSHOT` for balances
and ref lookups), so reads and writes never wait on each other, and a deadlock victim is retried.

## Database failures: retries and `Unavailable`

Transient database failures (a failed or dropped connection, a failover, a deadlock) are retried in Infrastructure with
Polly: up to 3 retries with exponential backoff and jitter, no new retry after 5 seconds, and none once the caller
cancels or its deadline passes.
Each read and the insert are retried as a whole (open the connection, run the command); the insert is safe to repeat
because a retry hits the unique key and the service returns the original movement, and a statement retries only its
opening, since lines already streamed can't be taken back.
Command timeouts aren't retried by default, because repeating a slow query adds load and multiplies the wait.
When retries run out, the caller gets `Unavailable` with a generic message (`BackendUnavailable` over WCF), so clients
know to try again later, instead of `Internal`, which means a bug.

## Indexes

The table is clustered on `(AccountId, Currency, OccurredAt, Id)`, declared `UNIQUE` because `Id` is in the key, so rows
are stored in ledger order: a balance is one range read, and a statement is one range read already in statement order,
with no sort, which is what lets it stream.
A unique index on `(AccountId, ExternalRef)` enforces the idempotency key and serves the duplicate-ref lookup, and a
nonclustered primary key on `Id` gives each row a stable identity.
Clustering on `Id` instead would have needed a second index that's almost a copy of the table to make balances and
statements fast.
The trade-offs: inserts land inside each account's range rather than at the end, so pages split a little more; the two
smaller indexes are larger because each carries the wide clustered key; and a balance still reads every row for its
account, so it slows as history grows (a stored running balance would fix that, at the cost of extra writes and a
frequently updated row per account).

## Statement export

`ExportStatement` streams a statement as a CSV file built from the same streamed statement, in 64 KB chunks, so the
server holds one chunk at a time: 150,000 lines (10.6 MB) export in about 1.5 s with live memory growing about 3 MB,
where a server that built the file first grew 11.5 MB and failed the volume test.
The start message carries the line count, from a count run inside the statement's own snapshot so it always matches,
which gives the client a real progress bar; the end message carries the closing balance, byte count and SHA-256.
The file is self-contained (opening, movement and closing rows), UTF-8 with a byte-order mark for Excel, escaped per
RFC 4180, and guarded against formula injection.
I chose streaming over an export job with a download link, which would need a job store, file storage and clean-up;
the trade-off is that the client must stay connected for the whole export.
The console client's `export` command writes to a `.partial` file with a live progress bar and keeps it only when the
checksum and counts match, deleting it on any failure or Ctrl+C.

## Structured logging

Logs are structured at the points that matter: the business events (a movement recorded, a retry replayed, a field
conflict as a warning, a statement completed or ended early), one summary per gRPC call with its status code and
duration, database retries and outages, and each WCF gateway outcome, all with fixed event IDs for filtering and alerts.
Every log written during a call carries a correlation ID, taken from the caller's `x-correlation-id` header or
generated, and returned in the trailers; the WCF gateway sends its own, so one legacy call can be followed across both
processes.
Output is JSON outside development and readable lines in development, using .NET's built-in formatter.
Account IDs are masked to their last 4 characters, and amounts, narrations and connection strings are never logged,
because the database is the financial record and logs are seen by many more people.
