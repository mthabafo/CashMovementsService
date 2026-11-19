-- Sample data for local development. Run after 001 and 002. Idempotent: rows are keyed on (AccountId, ExternalRef).

INSERT INTO dbo.Movement (AccountId, ExternalRef, Amount, Currency, OccurredAt, Narration)
SELECT s.AccountId, s.ExternalRef, s.Amount, s.Currency, s.OccurredAt, s.Narration
FROM (VALUES
    (N'ACC-1001', N'SEED-0001',  25000.00, 'ZAR', CAST('2026-10-01T08:00:00+02:00' AS DATETIMEOFFSET(7)), N'Salary'),
    (N'ACC-1001', N'SEED-0002',  -8500.00, 'ZAR', CAST('2026-10-02T09:15:00+02:00' AS DATETIMEOFFSET(7)), N'Rent'),
    (N'ACC-1001', N'SEED-0003',  -1249.99, 'ZAR', CAST('2026-10-05T17:40:00+02:00' AS DATETIMEOFFSET(7)), N'Groceries'),
    (N'ACC-1001', N'SEED-0004',   -650.00, 'ZAR', CAST('2026-10-15T12:00:00+02:00' AS DATETIMEOFFSET(7)), NULL),
    (N'ACC-1001', N'SEED-0005',  25000.00, 'ZAR', CAST('2026-11-01T08:00:00+02:00' AS DATETIMEOFFSET(7)), N'Salary'),
    (N'ACC-1001', N'SEED-0006',   1200.00, 'USD', CAST('2026-10-20T10:00:00+00:00' AS DATETIMEOFFSET(7)), N'Consulting invoice 42'),
    (N'ACC-1001', N'SEED-0007',   -300.00, 'USD', CAST('2026-11-03T10:00:00+00:00' AS DATETIMEOFFSET(7)), N'Transfer out'),
    (N'ACC-2002', N'SEED-0001',    500.00, 'ZAR', CAST('2026-10-10T11:30:00+02:00' AS DATETIMEOFFSET(7)), N'Opening deposit'),
    (N'ACC-2002', N'SEED-0002',   -750.00, 'ZAR', CAST('2026-10-12T14:00:00+02:00' AS DATETIMEOFFSET(7)), N'Card payment (overdrawn)')
) AS s (AccountId, ExternalRef, Amount, Currency, OccurredAt, Narration)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.Movement m
    WHERE m.AccountId = s.AccountId AND m.ExternalRef = s.ExternalRef
);
GO
