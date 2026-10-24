-- The legacy core banking system's tables.
-- Column names and order match the generator's CSV headers exactly (load-legacy.sh checks),
-- and the types are what an old core uses: dates as text, flags as Y/N, codes as short strings.
-- Stage 3 maps this into a clean target schema. Re-running this drops and recreates the tables.

IF DB_ID('CoreShift') IS NULL CREATE DATABASE CoreShift;
GO
-- A rehearsal copy, rebuilt from the extract on every load, so it needs no log backups.
-- SIMPLE recovery lets the bulk loads be minimally logged. Under the default FULL model, loading
-- 100,000 customers grew the log to 2 GB, and it would keep growing once anyone took a backup.
ALTER DATABASE CoreShift SET RECOVERY SIMPLE;
GO
USE CoreShift;
GO
-- Without this, an unclean SQL Server restart skips 1,000 ids and run #6 is followed by run #1006
ALTER DATABASE SCOPED CONFIGURATION SET IDENTITY_CACHE = OFF;
IF SCHEMA_ID('legacy') IS NULL EXEC('CREATE SCHEMA legacy');
GO

DROP TABLE IF EXISTS legacy.transactions, legacy.account_holders, legacy.accounts, legacy.customers;

CREATE TABLE legacy.customers (
    CUST_ID             VARCHAR(10)    NOT NULL CONSTRAINT pk_customers PRIMARY KEY,
    TITLE               VARCHAR(5)     NOT NULL,
    FIRST_NAME          VARCHAR(60)    NOT NULL,
    MIDDLE_NAME         VARCHAR(60)    NULL,
    LAST_NAME           VARCHAR(60)    NOT NULL,
    GENDER              CHAR(1)        NOT NULL,
    DATE_OF_BIRTH       VARCHAR(10)    NOT NULL,
    ADDRESS_LINE        VARCHAR(120)   NOT NULL,
    SUBURB              VARCHAR(60)    NOT NULL,
    STATE               VARCHAR(3)     NOT NULL,
    POSTCODE            CHAR(4)        NOT NULL,
    MOBILE              VARCHAR(15)    NULL,
    EMAIL               VARCHAR(120)   NULL,
    PREFERRED_CONTACT   VARCHAR(5)     NOT NULL,
    CUSTOMER_SINCE      VARCHAR(10)    NOT NULL,
    HOME_BRANCH         VARCHAR(5)     NOT NULL,
    EMPLOYMENT_STATUS   VARCHAR(15)    NOT NULL,
    OCCUPATION          VARCHAR(40)    NULL,
    EMPLOYER            VARCHAR(80)    NULL,
    ANNUAL_INCOME       DECIMAL(12,2)  NOT NULL,
    RESIDENTIAL_STATUS  VARCHAR(20)    NOT NULL,
    MARITAL_STATUS      VARCHAR(10)    NOT NULL,
    DEPENDANTS          TINYINT        NOT NULL,
    TFN_STATUS          VARCHAR(12)    NOT NULL,
    TAX_RESIDENCY       CHAR(2)        NOT NULL,
    KYC_STATUS          VARCHAR(16)    NOT NULL,
    KYC_ID_TYPE         VARCHAR(20)    NULL,
    KYC_VERIFIED_DATE   VARCHAR(10)    NULL,
    AML_RISK_RATING     VARCHAR(6)     NOT NULL,
    PEP                 CHAR(1)        NOT NULL,
    CREDIT_SCORE        SMALLINT       NULL,
    CREDIT_SCORE_DATE   VARCHAR(10)    NULL,
    CREDIT_ENQUIRIES5Y  TINYINT        NULL,
    CREDIT_DEFAULTS     TINYINT        NULL,
    BANKRUPT            CHAR(1)        NOT NULL,
    VULNERABLE          CHAR(1)        NOT NULL,
    DECEASED            CHAR(1)        NOT NULL,
    MARKETING_CONSENT   CHAR(1)        NOT NULL,
    ROW_VERSION         ROWVERSION     -- not in the extract: the freeze fingerprint, see the end of this file
);

CREATE TABLE legacy.accounts (
    ACCT_NO               VARCHAR(12)    NOT NULL CONSTRAINT pk_accounts PRIMARY KEY,
    BSB                   VARCHAR(7)     NOT NULL,
    PRODUCT_CODE          VARCHAR(5)     NOT NULL,
    PRODUCT_NAME          VARCHAR(40)    NOT NULL,
    ACCOUNT_NAME          VARCHAR(30)    NOT NULL,
    BRANCH                VARCHAR(5)     NOT NULL,
    OPEN_DATE             VARCHAR(10)    NOT NULL,
    CLOSE_DATE            VARCHAR(10)    NULL,
    STATUS                VARCHAR(7)     NOT NULL,
    BALANCE               DECIMAL(15,2)  NOT NULL,
    AVAILABLE_BALANCE     DECIMAL(15,2)  NOT NULL,
    INTEREST_RATE         DECIMAL(5,2)   NOT NULL,
    ACCRUED_INTEREST      DECIMAL(12,2)  NOT NULL,
    LAST_INTEREST_DATE    VARCHAR(10)    NULL,
    OVERDRAFT_LIMIT       DECIMAL(12,2)  NOT NULL,
    CREDIT_LIMIT          DECIMAL(12,2)  NULL,
    MASKED_CARD_NO        VARCHAR(19)    NULL,
    ORIGINAL_AMOUNT       DECIMAL(15,2)  NULL,
    TERM_MONTHS           SMALLINT       NULL,
    MATURITY_DATE         VARCHAR(10)    NULL,
    MATURITY_INSTRUCTION  VARCHAR(15)    NULL,
    REPAYMENT_AMOUNT      DECIMAL(12,2)  NULL,
    REPAYMENT_FREQUENCY   VARCHAR(11)    NULL,
    REPAYMENT_TYPE        VARCHAR(2)     NULL,
    NEXT_DUE_DATE         VARCHAR(10)    NULL,
    ARREARS_DAYS          SMALLINT       NULL,
    ARREARS_AMOUNT        DECIMAL(12,2)  NULL,
    REPAYMENT_HISTORY24M  CHAR(24)       NULL,
    HARDSHIP              CHAR(1)        NOT NULL,
    LOAN_PURPOSE          VARCHAR(20)    NULL,
    SECURITY_VALUE        DECIMAL(15,2)  NULL,
    ORIGINAL_LVR          DECIMAL(5,2)   NULL,
    FIXED_RATE_EXPIRY     VARCHAR(10)    NULL,
    LINKED_ACCT_NO        VARCHAR(12)    NULL,
    LAST_ACTIVITY_DATE    VARCHAR(10)    NULL,
    ROW_VERSION           ROWVERSION
);

-- Many-to-many: a joint account has two holders, a customer has several accounts
CREATE TABLE legacy.account_holders (
    ACCT_NO  VARCHAR(12)  NOT NULL CONSTRAINT fk_holders_account REFERENCES legacy.accounts (ACCT_NO),
    CUST_ID  VARCHAR(10)  NOT NULL CONSTRAINT fk_holders_customer REFERENCES legacy.customers (CUST_ID),
    ROLE     VARCHAR(7)   NOT NULL,
    ROW_VERSION ROWVERSION,
    CONSTRAINT pk_account_holders PRIMARY KEY (ACCT_NO, CUST_ID)
);
CREATE INDEX ix_holders_customer ON legacy.account_holders (CUST_ID);

CREATE TABLE legacy.transactions (
    TXN_ID           VARCHAR(11)    NOT NULL CONSTRAINT pk_transactions PRIMARY KEY,
    ACCT_NO          VARCHAR(12)    NOT NULL CONSTRAINT fk_transactions_account REFERENCES legacy.accounts (ACCT_NO),
    TXN_DATE         VARCHAR(10)    NOT NULL,
    POST_DATE        VARCHAR(10)    NULL,
    TXN_CODE         VARCHAR(3)     NOT NULL,
    DESCRIPTION      VARCHAR(120)   NOT NULL,
    AMOUNT           DECIMAL(15,2)  NOT NULL,
    RUNNING_BALANCE  DECIMAL(15,2)  NULL,
    STATUS           VARCHAR(7)     NOT NULL,
    CHANNEL          VARCHAR(13)    NOT NULL,
    ROW_VERSION      ROWVERSION
);
CREATE INDEX ix_transactions_account ON legacy.transactions (ACCT_NO);
GO

-- Every insert or update stamps a row with a new, ever-higher ROW_VERSION, so a table's row count and
-- highest stamp change whenever anything in it does: the freeze fingerprint. The extract has no such
-- column and BULK INSERT maps fields by position, so each table loads through a view of its other
-- columns, built here from the table itself, and rows are stamped as they go in. Adding the column
-- after the load instead rewrote every row: 43 s and 4 GB of log at 100,000 customers.
DECLARE @table sysname = '', @columns nvarchar(4000);
WHILE 1 = 1
BEGIN
    SELECT TOP 1 @table = name FROM sys.tables WHERE schema_id = SCHEMA_ID('legacy') AND name > @table ORDER BY name;
    IF @@ROWCOUNT = 0 BREAK;
    SELECT @columns = STRING_AGG(QUOTENAME(name), ', ') WITHIN GROUP (ORDER BY column_id)
    FROM sys.columns WHERE object_id = OBJECT_ID('legacy.' + @table) AND name <> 'ROW_VERSION';
    EXEC('CREATE OR ALTER VIEW legacy.' + @table + '_extract AS SELECT ' + @columns + ' FROM legacy.' + @table);
END
