-- CashMovements schema. Run against the target database (it does not create the database itself).
-- Idempotent: safe to run more than once.

-- Statements read the opening balance and the lines in one SNAPSHOT transaction so they always agree.
ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;
GO

-- READ_COMMITTED_SNAPSHOT: ordinary reads (balances, external-ref lookups) see the last committed version of each row
-- instead of taking shared locks, so they never wait for an insert and an insert never waits for them. Cheap here:
-- snapshot isolation already keeps row versions, and the ledger never updates or deletes, so there are almost no old
-- versions to store. Uniqueness is unaffected: the unique key is always checked with locks.
-- Switching it on needs the database to itself, so WITH ROLLBACK IMMEDIATE disconnects other sessions. That happens
-- only the first time: once it's on, the check skips it (and on Azure SQL Database, where it's on by default).
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_read_committed_snapshot_on = 1)
    ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO

IF OBJECT_ID(N'dbo.Movement', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Movement
    (
        Id          BIGINT IDENTITY(1, 1) NOT NULL,

        -- Binary collation: account IDs and external refs are case-sensitive in the domain,
        -- so 'acc1' and 'ACC1' must be different values here too (including in the unique constraint).
        AccountId   NVARCHAR(34) COLLATE Latin1_General_BIN2 NOT NULL,
        ExternalRef NVARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,

        -- Signed: + credit, - debit. Amount and currency are separate columns.
        Amount      DECIMAL(19, 2) NOT NULL,
        Currency    CHAR(3)        NOT NULL,

        OccurredAt  DATETIMEOFFSET(7) NOT NULL,
        Narration   NVARCHAR(140)     NULL,

        CONSTRAINT PK_Movement PRIMARY KEY NONCLUSTERED (Id),
        CONSTRAINT UQ_Movement_Account_ExternalRef UNIQUE (AccountId, ExternalRef),
        CONSTRAINT CK_Movement_Amount   CHECK (Amount <> 0),
        CONSTRAINT CK_Movement_Currency CHECK (Currency COLLATE Latin1_General_BIN2 LIKE '[A-Z][A-Z][A-Z]')
    );

    -- Rows are stored in ledger order: every balance and statement query is one contiguous range read,
    -- already in statement order (OccurredAt, then Id as the tie-break), so nothing has to be sorted.
    -- UNIQUE because Id is part of the key: declaring it tells SQL Server each key identifies exactly one row.
    CREATE UNIQUE CLUSTERED INDEX CX_Movement_Ledger
        ON dbo.Movement (AccountId, Currency, OccurredAt, Id);
END;
GO

-- Upgrade: databases created before the ledger index was declared UNIQUE. Rebuilds the index in place (the data
-- already satisfies it, since Id is unique); skipped once it's unique.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Movement') AND name = N'CX_Movement_Ledger' AND is_unique = 0)
    CREATE UNIQUE CLUSTERED INDEX CX_Movement_Ledger
        ON dbo.Movement (AccountId, Currency, OccurredAt, Id)
        WITH (DROP_EXISTING = ON);
GO
