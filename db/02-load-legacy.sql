-- Bulk-loads the generator's CSV extract into the legacy tables.
-- Parents go in before children so the foreign keys can be checked as each table loads.
-- CHECK_CONSTRAINTS matters: without it BULK INSERT skips foreign key checks entirely.
-- ROWTERMINATOR 0x0a because the files use Unix line endings ('\n' here would mean \r\n).

USE CoreShift;

BULK INSERT legacy.customers FROM '/coreshift/CoreShift.Generator/output/legacy/customers.csv'
    WITH (FORMAT = 'CSV', FIRSTROW = 2, ROWTERMINATOR = '0x0a', CHECK_CONSTRAINTS, TABLOCK);

BULK INSERT legacy.accounts FROM '/coreshift/CoreShift.Generator/output/legacy/accounts.csv'
    WITH (FORMAT = 'CSV', FIRSTROW = 2, ROWTERMINATOR = '0x0a', CHECK_CONSTRAINTS, TABLOCK);

BULK INSERT legacy.account_holders FROM '/coreshift/CoreShift.Generator/output/legacy/account_holders.csv'
    WITH (FORMAT = 'CSV', FIRSTROW = 2, ROWTERMINATOR = '0x0a', CHECK_CONSTRAINTS, TABLOCK);

BULK INSERT legacy.transactions FROM '/coreshift/CoreShift.Generator/output/legacy/transactions.csv'
    WITH (FORMAT = 'CSV', FIRSTROW = 2, ROWTERMINATOR = '0x0a', CHECK_CONSTRAINTS, TABLOCK);
