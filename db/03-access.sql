-- Run by db/load-legacy.sh as the administrator after every load: what the coreshift tool isn't
-- allowed to do itself. It sets the freeze on the legacy tables and the tool's own login, which can
-- read legacy but never change it, so no bug in the tool can damage the old bank.
-- Expects the scripting variable pw, the coreshift login's password from .env.

USE CoreShift;
GO
IF SCHEMA_ID('target') IS NULL EXEC('CREATE SCHEMA target');
IF SCHEMA_ID('ops') IS NULL EXEC('CREATE SCHEMA ops');
GO

-- The freeze switch: while frozen_by_run is set, the triggers below refuse every write to legacy.
-- A new legacy bank starts unfrozen; a cutover that retired the last one doesn't retire this one.
IF OBJECT_ID('ops.freeze') IS NULL CREATE TABLE ops.freeze (frozen_by_run INT NULL);
DELETE FROM ops.freeze;
INSERT INTO ops.freeze VALUES (NULL);
GO
CREATE OR ALTER TRIGGER legacy.freeze_customers ON legacy.customers AFTER INSERT, UPDATE, DELETE AS
    IF EXISTS (SELECT 1 FROM ops.freeze WHERE frozen_by_run IS NOT NULL)
        THROW 50100, 'Legacy is frozen for a cutover. Writes are blocked until it commits or rolls back.', 1;
GO
CREATE OR ALTER TRIGGER legacy.freeze_accounts ON legacy.accounts AFTER INSERT, UPDATE, DELETE AS
    IF EXISTS (SELECT 1 FROM ops.freeze WHERE frozen_by_run IS NOT NULL)
        THROW 50100, 'Legacy is frozen for a cutover. Writes are blocked until it commits or rolls back.', 1;
GO
CREATE OR ALTER TRIGGER legacy.freeze_account_holders ON legacy.account_holders AFTER INSERT, UPDATE, DELETE AS
    IF EXISTS (SELECT 1 FROM ops.freeze WHERE frozen_by_run IS NOT NULL)
        THROW 50100, 'Legacy is frozen for a cutover. Writes are blocked until it commits or rolls back.', 1;
GO
CREATE OR ALTER TRIGGER legacy.freeze_transactions ON legacy.transactions AFTER INSERT, UPDATE, DELETE AS
    IF EXISTS (SELECT 1 FROM ops.freeze WHERE frozen_by_run IS NOT NULL)
        THROW 50100, 'Legacy is frozen for a cutover. Writes are blocked until it commits or rolls back.', 1;
GO

-- The tool's login, with nothing it doesn't need
USE master;
IF SUSER_ID('coreshift') IS NULL CREATE LOGIN coreshift WITH PASSWORD = '$(pw)';
ELSE ALTER LOGIN coreshift WITH PASSWORD = '$(pw)';
GO
USE CoreShift;
IF USER_ID('coreshift') IS NULL CREATE USER coreshift FOR LOGIN coreshift;
GRANT SELECT ON SCHEMA::legacy TO coreshift;                                     -- read the old bank, never write it
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER, REFERENCES ON SCHEMA::target TO coreshift;  -- own the new bank, foreign keys included
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON SCHEMA::ops TO coreshift;         -- and the migration's bookkeeping
GRANT CREATE TABLE, CREATE VIEW TO coreshift;
