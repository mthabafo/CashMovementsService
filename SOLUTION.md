# CashMovements – solution notes

## Large statements (50,000+ rows): streaming

Each statement row flows one at a time: SQL Server reads in index order with no sort, Dapper hands each row on
unbuffered, the service keeps only the running balance, and gRPC sends one message per line as it's produced.
The trade-offs: one connection and snapshot transaction stay open for the whole stream (that's what keeps the balances
consistent), the opening-balance sum gets slower as the account's history grows, and backpressure from a slow client
over a real network relies on HTTP/2 by design and isn't tested.

## Validation

Validation is layered, with each rule written once in the domain and inherited by both the gRPC and WCF APIs.
The API layer only parses (culture-invariant amounts, required timestamps); value objects reject bad account IDs,
references and currencies when they're built, and `Movement` always enforces a non-zero amount with at most two decimal
places, whether created or read back, while the future-date rule applies only when recording.
The service treats a reused reference with the same amount and currency as a retry and a different one as a duplicate;
the database repeats the critical rules (CHECK constraints, `DECIMAL(19,2)`, a case-sensitive unique key) as a last
line of defence.
Bad input becomes `InvalidArgument` and duplicates `AlreadyExists`, but a corrupt stored row is reported as `Internal`
and logged, because the caller isn't at fault.

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

## Optimistic concurrency control

Concurrency control is optimistic: nothing is locked in advance, so a submission looks up its reference and inserts
without holding a lock in between, and the rare conflict is detected by the unique key at insert time and resolved as a
retry or `AlreadyExists`; reads use row versioning rather than shared locks.
The only locks are the brief exclusive ones SQL Server takes on a new row within a single `INSERT`.
This fits because conflicts are rare (almost always a client retry), pessimistic range locks would make every request
queue and risk deadlocks, and an append-only ledger with calculated balances has no lost updates to prevent.
There's no `rowversion` column because nothing is ever updated; if editing were added, an
`UPDATE ... WHERE RowVersion = @Expected` check would be the natural fit.

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
