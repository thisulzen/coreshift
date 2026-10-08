-- Integrity checks run after every load. Each one throws, failing the load, if it finds a problem.

SET NOCOUNT ON;
USE CoreShift;

-- Every balance equals the sum of its posted transactions
IF EXISTS (SELECT 1
           FROM legacy.accounts a
           LEFT JOIN (SELECT ACCT_NO, SUM(AMOUNT) AS total
                      FROM legacy.transactions
                      WHERE STATUS = 'POSTED'
                      GROUP BY ACCT_NO) t ON t.ACCT_NO = a.ACCT_NO
           WHERE a.BALANCE <> ISNULL(t.total, 0))
    THROW 50001, 'An account balance does not match its transaction history', 1;

-- BULK INSERT only checks foreign keys when told to (CHECK_CONSTRAINTS). If it skipped them,
-- SQL Server marks the keys "not trusted", so this catches a load that bypassed them.
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE is_not_trusted = 1 AND SCHEMA_NAME(schema_id) = 'legacy')
    THROW 50002, 'Foreign keys were not checked during the load', 1;

-- No transaction dated before its account opened. Account dates come in two formats:
-- CONVERT style 103 reads dd/MM/yyyy, style 112 reads yyyyMMdd.
-- Card purchases are excluded: their tap date can fall a day or two before settlement.
IF EXISTS (SELECT 1
           FROM legacy.transactions t
           JOIN legacy.accounts a ON a.ACCT_NO = t.ACCT_NO
           WHERE t.TXN_CODE <> 'POS'
             AND CONVERT(date, t.TXN_DATE, 103) <
                 CASE WHEN a.OPEN_DATE LIKE '%/%' THEN CONVERT(date, a.OPEN_DATE, 103) ELSE CONVERT(date, a.OPEN_DATE, 112) END)
    THROW 50003, 'A transaction is dated before its account opened', 1;

PRINT 'All integrity checks passed';
