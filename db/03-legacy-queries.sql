-- Practice queries against the legacy bank. Run them one at a time:
--   docker compose exec sql bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d CoreShift'
-- or connect from VS Code's SQL Server extension (localhost,1433, user sa, password in .env).

USE CoreShift;

-- Stage 2 "done when": total balance across all legacy accounts
SELECT COUNT(*) AS accounts, SUM(BALANCE) AS total_balance
FROM legacy.accounts;

-- That total nets deposits against loans, so split it: what the bank holds vs what it's owed
SELECT CASE WHEN BALANCE >= 0 THEN 'Deposits held' ELSE 'Owed to the bank' END AS side,
       COUNT(*) AS accounts, SUM(BALANCE) AS balance
FROM legacy.accounts
GROUP BY CASE WHEN BALANCE >= 0 THEN 'Deposits held' ELSE 'Owed to the bank' END;

-- Balance by product and status
SELECT PRODUCT_NAME, STATUS, COUNT(*) AS accounts, SUM(BALANCE) AS balance
FROM legacy.accounts
GROUP BY PRODUCT_NAME, STATUS
ORDER BY PRODUCT_NAME, STATUS;

-- One joint household's accounts: customers -> account_holders -> accounts
SELECT c.CUST_ID, c.FIRST_NAME, c.LAST_NAME, h.ROLE, a.ACCT_NO, a.PRODUCT_NAME, a.STATUS, a.BALANCE
FROM legacy.customers c
JOIN legacy.account_holders h ON h.CUST_ID = c.CUST_ID
JOIN legacy.accounts a ON a.ACCT_NO = h.ACCT_NO
WHERE c.CUST_ID IN (SELECT CUST_ID FROM legacy.account_holders
                    WHERE ACCT_NO = (SELECT TOP 1 ACCT_NO FROM legacy.account_holders WHERE ROLE = 'JOINT'))
ORDER BY c.CUST_ID, a.ACCT_NO;

-- Top 10 customers by deposits (a joint account counts in full for each holder)
SELECT TOP 10 c.CUST_ID, c.FIRST_NAME, c.LAST_NAME, SUM(a.BALANCE) AS deposits
FROM legacy.customers c
JOIN legacy.account_holders h ON h.CUST_ID = c.CUST_ID
JOIN legacy.accounts a ON a.ACCT_NO = h.ACCT_NO
WHERE a.BALANCE > 0
GROUP BY c.CUST_ID, c.FIRST_NAME, c.LAST_NAME
ORDER BY deposits DESC;

-- Loans and cards in arrears, worst first
SELECT a.ACCT_NO, a.PRODUCT_NAME, a.BALANCE, a.ARREARS_DAYS, a.ARREARS_AMOUNT, a.HARDSHIP, a.REPAYMENT_HISTORY24M
FROM legacy.accounts a
WHERE a.ARREARS_DAYS > 0
ORDER BY a.ARREARS_DAYS DESC;

-- A taste of Stage 4: every balance should equal the sum of its posted transactions.
-- Expect no rows.
SELECT a.ACCT_NO, a.BALANCE, t.posted_total
FROM legacy.accounts a
LEFT JOIN (SELECT ACCT_NO, SUM(AMOUNT) AS posted_total
           FROM legacy.transactions
           WHERE STATUS = 'POSTED'
           GROUP BY ACCT_NO) t ON t.ACCT_NO = a.ACCT_NO
WHERE a.BALANCE <> ISNULL(t.posted_total, 0);

-- Why the legacy data needs cleaning in Stage 3: the same field in two date formats
SELECT CASE WHEN OPEN_DATE LIKE '__/__/____' THEN 'dd/MM/yyyy' ELSE 'yyyyMMdd' END AS date_format,
       COUNT(*) AS accounts
FROM legacy.accounts
GROUP BY CASE WHEN OPEN_DATE LIKE '__/__/____' THEN 'dd/MM/yyyy' ELSE 'yyyyMMdd' END;

-- ...and names keyed in with stray spaces or all lowercase
-- (DATALENGTH counts trailing spaces; LEN and = comparisons ignore them)
SELECT CUST_ID, '[' + FIRST_NAME + ']' AS first_name, '[' + LAST_NAME + ']' AS last_name
FROM legacy.customers
WHERE DATALENGTH(FIRST_NAME) <> DATALENGTH(LTRIM(RTRIM(FIRST_NAME)))
   OR FIRST_NAME COLLATE Latin1_General_CS_AS = LOWER(FIRST_NAME);
