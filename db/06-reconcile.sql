-- Reconciliation: compares legacy with target and records every difference as a break with a reason.
-- It reads both sides with its own SQL and never calls the C# mapping code, so a bug in the
-- mapping can't hide itself. Expects @rehearsal_id, a row already in ops.rehearsals.
SET NOCOUNT ON;
DROP TABLE IF EXISTS #la, #lh, #th;
DECLARE @run_id int = (SELECT run_id FROM ops.rehearsals WHERE rehearsal_id = @rehearsal_id);

-- Legacy accounts with dates parsed from either format: style 103 is dd/MM/yyyy, 112 is yyyyMMdd
SELECT ACCT_NO, BALANCE, ACCRUED_INTEREST,
       CASE WHEN OPEN_DATE LIKE '%/%' THEN CONVERT(date, OPEN_DATE, 103) ELSE CONVERT(date, OPEN_DATE, 112) END AS opened_on
INTO #la FROM legacy.accounts;

-- Each account's whole history as one SHA-256: every transaction's id, dates, amount, code and
-- status, in id order. One changed cent, date or row anywhere changes the hash.
SELECT ACCT_NO, COUNT(*) AS txns, SUM(CASE WHEN STATUS = 'PENDING' THEN 1 ELSE 0 END) AS pending,
       HASHBYTES('SHA2_256', STRING_AGG(CAST(CONCAT(TXN_ID, '|', CONVERT(char(10), CONVERT(date, TXN_DATE, 103), 23), '|',
           CONVERT(char(10), CONVERT(date, POST_DATE, 103), 23), '|', AMOUNT, '|', TXN_CODE, '|', STATUS) AS varchar(max)), ';')
           WITHIN GROUP (ORDER BY TXN_ID)) AS history
INTO #lh FROM legacy.transactions GROUP BY ACCT_NO;

SELECT account_number, COUNT(*) AS txns, SUM(CASE WHEN status = 'Pending' THEN 1 ELSE 0 END) AS pending,
       HASHBYTES('SHA2_256', STRING_AGG(CAST(CONCAT(transaction_id, '|', CONVERT(char(10), transaction_date, 23), '|',
           CONVERT(char(10), posted_on, 23), '|', amount, '|', code, '|', UPPER(status)) AS varchar(max)), ';')
           WITHIN GROUP (ORDER BY transaction_id)) AS history
INTO #th FROM target.transactions GROUP BY account_number;

INSERT INTO ops.breaks (rehearsal_id, check_name, entity_key, legacy_value, target_value, reason, detail, explained)
SELECT @rehearsal_id, 'account exists', l.ACCT_NO, 'present', 'missing', 'Account not migrated', NULL, 0
FROM #la l LEFT JOIN target.accounts t ON t.account_number = l.ACCT_NO WHERE t.account_number IS NULL
UNION ALL
SELECT @rehearsal_id, 'account exists', t.account_number, 'missing', 'present', 'Account has no legacy source', NULL, 0
FROM target.accounts t LEFT JOIN #la l ON l.ACCT_NO = t.account_number WHERE l.ACCT_NO IS NULL
UNION ALL
-- Balances, to the cent
SELECT @rehearsal_id, 'balance', l.ACCT_NO, CONVERT(varchar(30), l.BALANCE), CONVERT(varchar(30), t.balance),
       -- Whole dollars within 50 cents of the truth: rounding, whichever way the code broke ties
       CASE WHEN t.balance = ROUND(t.balance, 0) AND ABS(t.balance - l.BALANCE) <= 0.5 THEN 'Balance rounded to whole dollars' ELSE 'Balance differs' END,
       CONCAT('Off by ', t.balance - l.BALANCE), 0
FROM #la l JOIN target.accounts t ON t.account_number = l.ACCT_NO WHERE t.balance <> l.BALANCE
UNION ALL
SELECT @rehearsal_id, 'accrued interest', l.ACCT_NO, CONVERT(varchar(30), l.ACCRUED_INTEREST), CONVERT(varchar(30), t.accrued_interest),
       CASE WHEN t.accrued_interest = 0 THEN 'Interest accrual timing: accrued interest not carried' ELSE 'Accrued interest differs' END,
       CONCAT('Off by ', t.accrued_interest - l.ACCRUED_INTEREST), 0
FROM #la l JOIN target.accounts t ON t.account_number = l.ACCT_NO WHERE t.accrued_interest <> l.ACCRUED_INTEREST
UNION ALL
SELECT @rehearsal_id, 'open date', l.ACCT_NO, CONVERT(char(10), l.opened_on, 23), CONVERT(char(10), t.opened_on, 23),
       -- Compared part by part: building the swapped date can fail, because SQL Server may evaluate it before any DAY() <= 12 guard
       CASE WHEN YEAR(t.opened_on) = YEAR(l.opened_on) AND MONTH(t.opened_on) = DAY(l.opened_on) AND DAY(t.opened_on) = MONTH(l.opened_on)
            THEN 'Date read month-first (MM/dd instead of dd/MM)' ELSE 'Open date differs' END, NULL, 0
FROM #la l JOIN target.accounts t ON t.account_number = l.ACCT_NO WHERE t.opened_on <> l.opened_on
UNION ALL
-- Transaction history: the count first, then the hash when the counts agree
SELECT @rehearsal_id, 'transaction count', COALESCE(lh.ACCT_NO, th.account_number), CONVERT(varchar(12), ISNULL(lh.txns, 0)), CONVERT(varchar(12), ISNULL(th.txns, 0)),
       CASE WHEN ISNULL(lh.txns, 0) - ISNULL(th.txns, 0) = ISNULL(lh.pending, 0) - ISNULL(th.pending, 0) AND ISNULL(lh.pending, 0) > ISNULL(th.pending, 0)
                 THEN 'Pending transactions not migrated'
            WHEN ISNULL(lh.txns, 0) > ISNULL(th.txns, 0) THEN 'Transactions missing'
            ELSE 'Extra transactions in target' END,
       CONCAT(ABS(ISNULL(lh.txns, 0) - ISNULL(th.txns, 0)), ' transaction(s)'), 0
FROM #lh lh FULL JOIN #th th ON th.account_number = lh.ACCT_NO WHERE ISNULL(lh.txns, 0) <> ISNULL(th.txns, 0)
UNION ALL
SELECT @rehearsal_id, 'history hash', lh.ACCT_NO, CONVERT(varchar(64), lh.history, 2), CONVERT(varchar(64), th.history, 2),
       'Transaction history altered', 'Same number of transactions, different content', 0
FROM #lh lh JOIN #th th ON th.account_number = lh.ACCT_NO WHERE lh.txns = th.txns AND lh.history <> th.history
UNION ALL
-- Status through the reconciliation's own lookup, not the mapping file's: a closed account must not come back open
SELECT @rehearsal_id, 'status', l.ACCT_NO, l.STATUS, t.status, 'Account status differs', NULL, 0
FROM legacy.accounts l JOIN target.accounts t ON t.account_number = l.ACCT_NO
WHERE t.status IS DISTINCT FROM CASE l.STATUS WHEN 'OPEN' THEN 'Active' WHEN 'CLOSED' THEN 'Closed' WHEN 'DORMANT' THEN 'Dormant' WHEN 'FROZEN' THEN 'Frozen' END
UNION ALL
-- The terms each customer is held to
SELECT @rehearsal_id, 'account terms', l.ACCT_NO, NULL, NULL, 'Account terms differ', d.fields, 0
FROM legacy.accounts l JOIN target.accounts t ON t.account_number = l.ACCT_NO
CROSS APPLY (SELECT CONCAT_WS(', ',
    CASE WHEN t.interest_rate <> l.INTEREST_RATE THEN 'interest rate' END,
    CASE WHEN t.overdraft_limit <> l.OVERDRAFT_LIMIT THEN 'overdraft limit' END,
    CASE WHEN t.credit_limit IS DISTINCT FROM l.CREDIT_LIMIT THEN 'credit limit' END,
    CASE WHEN t.available_balance <> l.AVAILABLE_BALANCE THEN 'available balance' END,
    CASE WHEN t.repayment_amount IS DISTINCT FROM l.REPAYMENT_AMOUNT THEN 'repayment' END,
    CASE WHEN t.in_hardship <> IIF(l.HARDSHIP = 'Y', 1, 0) THEN 'hardship' END) AS fields) d
WHERE d.fields <> ''
UNION ALL
-- Who owns each account: every legacy holder, through the customer map, with the same role
SELECT @rehearsal_id, 'account holder', COALESCE(e.ACCT_NO, t.account_number), CONCAT(e.customer_id, ' ', e.role), CONCAT(t.customer_id, ' ', t.role),
       CASE WHEN t.account_number IS NULL THEN CONCAT(e.role, ' holder not migrated')
            WHEN e.ACCT_NO IS NULL THEN 'Holder has no legacy source' ELSE 'Holder role differs' END, NULL, 0
FROM (SELECT h.ACCT_NO, m.customer_id, CASE h.ROLE WHEN 'PRIMARY' THEN 'Primary' WHEN 'JOINT' THEN 'Joint' END AS role
      FROM legacy.account_holders h JOIN ops.customer_map m ON m.legacy_cust_id = h.CUST_ID) e
FULL JOIN target.account_holders t ON t.account_number = e.ACCT_NO AND t.customer_id = e.customer_id
WHERE t.account_number IS NULL OR e.ACCT_NO IS NULL OR t.role IS DISTINCT FROM e.role
UNION ALL
-- Compliance flags must survive, merges included: if any legacy record of a person says PEP, vulnerable,
-- deceased or bankrupt, so must the target customer, and their AML risk rating can't drop
SELECT @rehearsal_id, 'compliance flags', l.CUST_ID, NULL, m.customer_id, 'Compliance flag lost', d.lost, 0
FROM legacy.customers l
JOIN ops.customer_map m ON m.legacy_cust_id = l.CUST_ID
JOIN target.customers t ON t.customer_id = m.customer_id
CROSS APPLY (SELECT CONCAT_WS(', ',
    CASE WHEN l.PEP = 'Y' AND t.is_pep = 0 THEN 'PEP' END,
    CASE WHEN l.VULNERABLE = 'Y' AND t.is_vulnerable = 0 THEN 'vulnerable' END,
    CASE WHEN l.DECEASED = 'Y' AND t.is_deceased = 0 THEN 'deceased' END,
    CASE WHEN l.BANKRUPT = 'Y' AND t.is_bankrupt = 0 THEN 'bankrupt' END,
    CASE WHEN CASE l.AML_RISK_RATING WHEN 'LOW' THEN 1 WHEN 'MEDIUM' THEN 2 ELSE 3 END
            > CASE t.aml_risk_rating WHEN 'LOW' THEN 1 WHEN 'MEDIUM' THEN 2 ELSE 3 END
         THEN CONCAT('AML risk ', l.AML_RISK_RATING, ' to ', t.aml_risk_rating) END) AS lost) d
WHERE d.lost <> ''
UNION ALL
-- Duplicate customers merged on purpose: expected, and each one carries its evidence
SELECT @rehearsal_id, 'customer merge', m.legacy_cust_id, m.legacy_cust_id, m.customer_id, 'Duplicate customer merged', m.match_reason, 1
FROM ops.customer_map m WHERE m.customer_id <> m.legacy_cust_id
UNION ALL
-- Every legacy customer must end up as a target customer, directly or through a merge
SELECT @rehearsal_id, 'customer exists', c.CUST_ID, 'present', 'missing', 'Customer not migrated', NULL, 0
FROM legacy.customers c LEFT JOIN ops.customer_map m ON m.legacy_cust_id = c.CUST_ID
LEFT JOIN target.customers t ON t.customer_id = m.customer_id WHERE t.customer_id IS NULL
UNION ALL
-- During a cutover, legacy must be exactly as it was when it was extracted
SELECT @rehearsal_id, 'freeze', 'legacy', r.legacy_fingerprint, f.fingerprint, 'Legacy changed during the freeze', NULL, 0
FROM ops.cutover_runs r CROSS JOIN ops.legacy_fingerprint f WHERE r.run_id = @run_id AND r.legacy_fingerprint <> f.fingerprint;

UPDATE ops.rehearsals SET
    legacy_customers    = (SELECT COUNT(*) FROM legacy.customers),
    target_customers    = (SELECT COUNT(*) FROM target.customers),
    merged_customers    = (SELECT COUNT(*) FROM ops.customer_map WHERE customer_id <> legacy_cust_id),
    legacy_accounts     = (SELECT COUNT(*) FROM legacy.accounts),
    target_accounts     = (SELECT COUNT(*) FROM target.accounts),
    legacy_transactions = (SELECT COUNT(*) FROM legacy.transactions),
    target_transactions = (SELECT COUNT(*) FROM target.transactions),
    legacy_balance      = (SELECT ISNULL(SUM(BALANCE), 0) FROM legacy.accounts),
    target_balance      = (SELECT ISNULL(SUM(balance), 0) FROM target.accounts),
    breaks              = (SELECT COUNT(*) FROM ops.breaks WHERE rehearsal_id = @rehearsal_id),
    unexplained         = (SELECT COUNT(*) FROM ops.breaks WHERE rehearsal_id = @rehearsal_id AND explained = 0),
    finished_at         = SYSUTCDATETIME()
WHERE rehearsal_id = @rehearsal_id;

-- Go needs zero unexplained breaks and the bank-wide totals to agree
UPDATE ops.rehearsals SET passed = CASE WHEN unexplained = 0 AND legacy_balance = target_balance
                                          AND legacy_accounts = target_accounts AND legacy_transactions = target_transactions THEN 1 ELSE 0 END
WHERE rehearsal_id = @rehearsal_id;
