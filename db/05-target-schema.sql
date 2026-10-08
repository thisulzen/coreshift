-- The target bank and the migration's own bookkeeping. The migrator runs this on every start.
-- It only creates what's missing, so running it again changes nothing.

IF SCHEMA_ID('target') IS NULL EXEC('CREATE SCHEMA target');
IF SCHEMA_ID('ops') IS NULL EXEC('CREATE SCHEMA ops');
GO

-- The clean bank: real types, one row per real person, product codes from the new catalogue
IF OBJECT_ID('target.customers') IS NULL
CREATE TABLE target.customers (
    customer_id          VARCHAR(10)    NOT NULL CONSTRAINT pk_target_customers PRIMARY KEY,
    title                VARCHAR(5)     NOT NULL,
    first_name           VARCHAR(60)    NOT NULL,
    middle_name          VARCHAR(60)    NULL,
    last_name            VARCHAR(60)    NOT NULL,
    gender               CHAR(1)        NOT NULL,
    date_of_birth        DATE           NOT NULL,
    address_line         VARCHAR(120)   NOT NULL,
    suburb               VARCHAR(60)    NOT NULL,
    state                VARCHAR(3)     NOT NULL,
    postcode             CHAR(4)        NOT NULL,
    mobile               VARCHAR(12)    NULL,          -- +61 and nine digits
    email                VARCHAR(120)   NULL,
    preferred_contact    VARCHAR(5)     NOT NULL,
    customer_since       DATE           NOT NULL,
    home_branch          VARCHAR(5)     NOT NULL,
    employment_status    VARCHAR(15)    NOT NULL,
    occupation           VARCHAR(40)    NULL,
    employer             VARCHAR(80)    NULL,
    annual_income        DECIMAL(12,2)  NOT NULL,
    residential_status   VARCHAR(20)    NOT NULL,
    marital_status       VARCHAR(10)    NOT NULL,
    dependants           TINYINT        NOT NULL,
    tfn_status           VARCHAR(12)    NOT NULL,
    tax_residency        CHAR(2)        NOT NULL,
    kyc_status           VARCHAR(16)    NOT NULL,
    kyc_id_type          VARCHAR(20)    NULL,
    kyc_verified_on      DATE           NULL,
    aml_risk_rating      VARCHAR(6)     NOT NULL,
    is_pep               BIT            NOT NULL,
    credit_score         SMALLINT       NULL,
    credit_score_on      DATE           NULL,
    credit_enquiries_5y  TINYINT        NULL,
    credit_defaults      TINYINT        NULL,
    is_bankrupt          BIT            NOT NULL,
    is_vulnerable        BIT            NOT NULL,
    is_deceased          BIT            NOT NULL,
    marketing_consent    BIT            NOT NULL
);

IF OBJECT_ID('target.accounts') IS NULL
CREATE TABLE target.accounts (
    account_number         CHAR(9)        NOT NULL CONSTRAINT pk_target_accounts PRIMARY KEY,
    bsb                    CHAR(7)        NOT NULL,
    product_code           VARCHAR(12)    NOT NULL,
    product_name           VARCHAR(40)    NOT NULL,
    account_name           VARCHAR(80)    NOT NULL,
    branch                 VARCHAR(5)     NOT NULL,
    opened_on              DATE           NOT NULL,
    closed_on              DATE           NULL,
    status                 VARCHAR(8)     NOT NULL,
    balance                DECIMAL(15,2)  NOT NULL,
    available_balance      DECIMAL(15,2)  NOT NULL,
    interest_rate          DECIMAL(5,2)   NOT NULL,
    accrued_interest       DECIMAL(12,2)  NOT NULL,
    last_interest_on       DATE           NULL,
    overdraft_limit        DECIMAL(12,2)  NOT NULL,
    credit_limit           DECIMAL(12,2)  NULL,
    masked_card_no         VARCHAR(19)    NULL,
    original_amount        DECIMAL(15,2)  NULL,
    term_months            SMALLINT       NULL,
    maturity_on            DATE           NULL,
    maturity_instruction   VARCHAR(15)    NULL,
    repayment_amount       DECIMAL(12,2)  NULL,
    repayment_frequency    VARCHAR(11)    NULL,
    repayment_type         VARCHAR(2)     NULL,
    next_due_on            DATE           NULL,
    arrears_days           SMALLINT       NULL,
    arrears_amount         DECIMAL(12,2)  NULL,
    repayment_history      CHAR(24)       NULL,
    in_hardship            BIT            NOT NULL,
    loan_purpose           VARCHAR(20)    NULL,
    security_value         DECIMAL(15,2)  NULL,
    original_lvr           DECIMAL(5,2)   NULL,
    fixed_rate_expiry      DATE           NULL,
    linked_account_number  CHAR(9)        NULL,
    last_activity_on       DATE           NULL
);

IF OBJECT_ID('target.account_holders') IS NULL
CREATE TABLE target.account_holders (
    account_number  CHAR(9)      NOT NULL CONSTRAINT fk_target_holders_account REFERENCES target.accounts (account_number),
    customer_id     VARCHAR(10)  NOT NULL CONSTRAINT fk_target_holders_customer REFERENCES target.customers (customer_id),
    role            VARCHAR(7)   NOT NULL,
    CONSTRAINT pk_target_account_holders PRIMARY KEY (account_number, customer_id)
);

IF OBJECT_ID('target.transactions') IS NULL
BEGIN
    CREATE TABLE target.transactions (
        transaction_id    VARCHAR(11)    NOT NULL CONSTRAINT pk_target_transactions PRIMARY KEY,
        account_number    CHAR(9)        NOT NULL CONSTRAINT fk_target_transactions_account REFERENCES target.accounts (account_number),
        transaction_date  DATE           NOT NULL,
        posted_on         DATE           NULL,
        code              VARCHAR(3)     NOT NULL,
        description       VARCHAR(120)   NOT NULL,
        amount            DECIMAL(15,2)  NOT NULL,
        running_balance   DECIMAL(15,2)  NULL,
        status            VARCHAR(7)     NOT NULL,
        channel           VARCHAR(13)    NOT NULL
    );
    CREATE INDEX ix_target_transactions_account ON target.transactions (account_number);
END

-- Which legacy customer became which target customer. Duplicates point at the record that survived.
IF OBJECT_ID('ops.customer_map') IS NULL
CREATE TABLE ops.customer_map (
    legacy_cust_id  VARCHAR(10)   NOT NULL CONSTRAINT pk_customer_map PRIMARY KEY,
    customer_id     VARCHAR(10)   NOT NULL,
    match_reason    VARCHAR(200)  NULL
);

-- Cutover runs, their audit log and their cohorts
IF OBJECT_ID('ops.cutover_runs') IS NULL
CREATE TABLE ops.cutover_runs (
    run_id              INT IDENTITY  CONSTRAINT pk_cutover_runs PRIMARY KEY,
    kind                VARCHAR(9)    NOT NULL,     -- REHEARSAL or CUTOVER
    state               VARCHAR(12)   NOT NULL,
    outcome             VARCHAR(5)    NULL,         -- GO or NO_GO
    mapping_version     VARCHAR(20)   NOT NULL,
    fault               VARCHAR(20)   NULL,
    customers_loaded    BIT           NOT NULL DEFAULT 0,
    legacy_fingerprint  VARCHAR(60)   NULL,
    legacy_accounts     INT           NULL,
    legacy_balance      DECIMAL(18,2) NULL,
    rehearsal_id        INT           NULL,
    started_at          DATETIME2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at          DATETIME2(0)  NOT NULL DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('ops.cutover_log') IS NULL
CREATE TABLE ops.cutover_log (
    log_id   INT IDENTITY  CONSTRAINT pk_cutover_log PRIMARY KEY,
    run_id   INT           NOT NULL,
    at       DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
    step     VARCHAR(12)   NOT NULL,
    message  VARCHAR(400)  NOT NULL
);

IF OBJECT_ID('ops.cohorts') IS NULL
CREATE TABLE ops.cohorts (
    run_id         INT           NOT NULL,
    cohort_no      INT           NOT NULL,
    first_account  CHAR(9)       NOT NULL,
    last_account   CHAR(9)       NOT NULL,
    accounts       INT           NOT NULL,
    loaded_at      DATETIME2(3)  NULL,
    CONSTRAINT pk_cohorts PRIMARY KEY (run_id, cohort_no)
);

-- While frozen_by_run is set, triggers on the legacy tables refuse every write
IF OBJECT_ID('ops.freeze') IS NULL
BEGIN
    CREATE TABLE ops.freeze (frozen_by_run INT NULL);
    INSERT INTO ops.freeze VALUES (NULL);
END

-- Reconciliation results: one row per rehearsal, one row per break
IF OBJECT_ID('ops.rehearsals') IS NULL
CREATE TABLE ops.rehearsals (
    rehearsal_id         INT IDENTITY  CONSTRAINT pk_rehearsals PRIMARY KEY,
    run_id               INT           NULL,
    mapping_version      VARCHAR(20)   NOT NULL,
    fault                VARCHAR(20)   NULL,
    started_at           DATETIME2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    finished_at          DATETIME2(0)  NULL,
    legacy_customers     INT           NULL,
    target_customers     INT           NULL,
    merged_customers     INT           NULL,
    legacy_accounts      INT           NULL,
    target_accounts      INT           NULL,
    legacy_transactions  INT           NULL,
    target_transactions  INT           NULL,
    legacy_balance       DECIMAL(18,2) NULL,
    target_balance       DECIMAL(18,2) NULL,
    breaks               INT           NULL,
    unexplained          INT           NULL,
    passed               BIT           NULL
);

IF OBJECT_ID('ops.breaks') IS NULL
BEGIN
    CREATE TABLE ops.breaks (
        break_id      INT IDENTITY  CONSTRAINT pk_breaks PRIMARY KEY,
        rehearsal_id  INT           NOT NULL,
        check_name    VARCHAR(30)   NOT NULL,
        entity_key    VARCHAR(20)   NOT NULL,
        legacy_value  VARCHAR(100)  NULL,
        target_value  VARCHAR(100)  NULL,
        reason        VARCHAR(80)   NOT NULL,
        detail        VARCHAR(200)  NULL,
        explained     BIT           NOT NULL
    );
    CREATE INDEX ix_breaks_rehearsal ON ops.breaks (rehearsal_id);
END
GO

-- One value that changes when any legacy row changes. CHECKSUM can, very rarely, miss a change;
-- the per-account SHA-256 history hashes in the reconciliation are the strong check.
CREATE OR ALTER VIEW ops.legacy_fingerprint AS
SELECT CONCAT((SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM legacy.customers), '/',
              (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM legacy.accounts), '/',
              (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM legacy.account_holders), '/',
              (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM legacy.transactions)) AS fingerprint;
GO

-- The freeze. db/load-legacy.sh recreates the legacy tables, which drops these, so they're
-- recreated here on every start.
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
