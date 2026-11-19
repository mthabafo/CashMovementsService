-- Stored procedures used by MovementRepository. Run after 001_schema.sql. Idempotent (CREATE OR ALTER).
-- Typed parameters match the column types exactly, so every lookup is an index seek with no implicit conversions.

CREATE OR ALTER PROCEDURE dbo.usp_Movement_FindByExternalRef
    @AccountId   NVARCHAR(34),
    @ExternalRef NVARCHAR(64)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, AccountId, ExternalRef, Amount, Currency, OccurredAt, Narration
    FROM dbo.Movement
    WHERE AccountId = @AccountId
      AND ExternalRef = @ExternalRef;
END;
GO

-- Returns the inserted row, including its new Id.
-- A duplicate (AccountId, ExternalRef) fails with error 2627 (unique constraint).
CREATE OR ALTER PROCEDURE dbo.usp_Movement_Insert
    @AccountId   NVARCHAR(34),
    @ExternalRef NVARCHAR(64),
    @Amount      DECIMAL(19, 2),
    @Currency    CHAR(3),
    @OccurredAt  DATETIMEOFFSET(7),
    @Narration   NVARCHAR(140) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.Movement (AccountId, ExternalRef, Amount, Currency, OccurredAt, Narration)
    OUTPUT inserted.Id, inserted.AccountId, inserted.ExternalRef, inserted.Amount,
           inserted.Currency, inserted.OccurredAt, inserted.Narration
    VALUES (@AccountId, @ExternalRef, @Amount, @Currency, @OccurredAt, @Narration);
END;
GO

-- Sum of signed amounts: the current balance when @Before is NULL, otherwise the balance just before @Before
-- (a statement's opening balance). Returns 0 when there are no movements.
CREATE OR ALTER PROCEDURE dbo.usp_Movement_SumAmounts
    @AccountId NVARCHAR(34),
    @Currency  CHAR(3),
    @Before    DATETIMEOFFSET(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Two separate statements rather than "(@Before IS NULL OR OccurredAt < @Before)",
    -- so each gets a clean range seek on the clustered index.
    IF @Before IS NULL
        SELECT COALESCE(SUM(Amount), 0) AS Amount
        FROM dbo.Movement
        WHERE AccountId = @AccountId
          AND Currency = @Currency;
    ELSE
        SELECT COALESCE(SUM(Amount), 0) AS Amount
        FROM dbo.Movement
        WHERE AccountId = @AccountId
          AND Currency = @Currency
          AND OccurredAt < @Before;
END;
GO

-- Movements in [@From, @To), in statement order. The ORDER BY matches the clustered index, so rows are
-- returned as they are read (no sort), which is what lets the statement stream to the client.
CREATE OR ALTER PROCEDURE dbo.usp_Movement_ListInPeriod
    @AccountId NVARCHAR(34),
    @Currency  CHAR(3),
    @From      DATETIMEOFFSET(7),
    @To        DATETIMEOFFSET(7)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, AccountId, ExternalRef, Amount, Currency, OccurredAt, Narration
    FROM dbo.Movement
    WHERE AccountId = @AccountId
      AND Currency = @Currency
      AND OccurredAt >= @From
      AND OccurredAt < @To
    ORDER BY OccurredAt, Id;
END;
GO
