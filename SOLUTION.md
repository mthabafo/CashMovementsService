# CashMovements – solution notes

## Large statements (50,000+ rows): streaming

Each statement row flows one at a time: SQL Server reads in index order with no sort, Dapper hands each row on
unbuffered, the service keeps only the running balance, and gRPC sends one message per line as it's produced.
The volume test shows it: at 150,000 lines the first line arrives in about 20 ms and live memory stays flat at about
6 MB, while a deliberately buffering server reached 58 MB and failed the test.
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
