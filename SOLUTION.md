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
