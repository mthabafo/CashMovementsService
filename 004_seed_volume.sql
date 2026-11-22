-- Volume data for local development only: account ACC-VOLUME gets 150,000 ZAR movements (January to July 2026), so a
-- large statement or export can be tried by hand, e.g. the client's "export ACC-VOLUME ZAR --from 2026-01-01".
-- Run by the development auto-setup (Database:AutoSetup); not used by the test databases.
-- Idempotent: skipped once the account has any movements. Set-based, so the first run takes a second or two.

IF NOT EXISTS (SELECT 1 FROM dbo.Movement WHERE AccountId = N'ACC-VOLUME')
BEGIN
    WITH numbers AS (
        SELECT TOP (150000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
        FROM sys.all_objects a CROSS JOIN sys.all_objects b)
    INSERT INTO dbo.Movement (AccountId, ExternalRef, Amount, Currency, OccurredAt, Narration)
    SELECT
        N'ACC-VOLUME',
        CONCAT(N'VOL-', FORMAT(n, 'D6')),
        -- Mostly small card payments, with a salary every 5,000 movements.
        CASE WHEN n % 5000 = 1 THEN 45000.00 ELSE -CAST((n % 997) + 1.25 AS DECIMAL(19, 2)) END,
        'ZAR',
        DATEADD(MINUTE, 2 * CAST(n AS INT), CAST('2026-01-01T00:00:00+02:00' AS DATETIMEOFFSET(7))),
        -- Some narrations contain commas and quotes, which the CSV export must escape.
        CASE WHEN n % 5000 = 1 THEN N'Salary'
             WHEN n % 7 = 0 THEN CONCAT(N'Card payment, store ', n % 50)
             WHEN n % 11 = 0 THEN N'Coffee at "The Bean"'
             ELSE CONCAT(N'Card payment ', n % 50) END
    FROM numbers;
END;
GO
