using System.Data;
using Microsoft.Data.SqlClient;

namespace CoreShift.Migrator;

// Moves the legacy bank into the target schema. Customers go first, with duplicates merged. Accounts
// then move in cohorts: each cohort's accounts, holders and transactions are deleted from the target
// and inserted again inside one transaction, so re-running a cohort, or everything, changes nothing.
class Migration(SqlConnection db, MappingConfig config, string? fault)
{
    // Deliberate mapping bugs, so the reconciliation can be shown catching each one
    public static readonly string[] Faults = ["rounding", "drop-pending", "date-format", "accrual", "joint-holders", "flags"];

    public void ClearTarget(SqlTransaction tx)
    {
        // Nothing points at transactions or holders, so they're truncated: near-instant and barely logged, still
        // inside the transaction. Deleting 11.6 million transactions took over 4 minutes at 100,000 customers.
        // TRUNCATE refuses accounts and customers, which foreign keys point at, but they're small.
        foreach (var sql in new[] { "TRUNCATE TABLE target.transactions", "TRUNCATE TABLE target.account_holders", "DELETE FROM target.accounts", "DELETE FROM target.customers" })
            Db.Cmd(db, sql, tx).ExecuteNonQuery();
        Db.Cmd(db, "DELETE FROM ops.target_load; INSERT INTO ops.target_load VALUES (@version, @fault)", tx, ("@version", config.Version), ("@fault", fault)).ExecuteNonQuery();
    }

    // Decides which legacy customers are the same person, and records it in ops.customer_map
    public int BuildCustomerMap(SqlTransaction tx)
    {
        var candidates = new List<Dedupe.Candidate>();
        using (var r = Db.Cmd(db, "SELECT CUST_ID, FIRST_NAME, LAST_NAME, DATE_OF_BIRTH, POSTCODE, SUBURB, CUSTOMER_SINCE, MOBILE, EMAIL FROM legacy.customers", tx).ExecuteReader())
            while (r.Read())
                candidates.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), Rules.LegacyDate(r.GetString(3))!.Value, r.GetString(4), r.GetString(5),
                    Rules.LegacyDate(r.GetString(6))!.Value, Rules.Mobile(Rules.Text(r[7])), Rules.Text(r[8])?.ToLowerInvariant()));

        var merged = Dedupe.Find(candidates);
        var map = Db.Shape(db, "ops.customer_map", tx);
        foreach (var c in candidates)
            map.Rows.Add(c.Id, merged.TryGetValue(c.Id, out var m) ? m.Survivor : c.Id, merged.TryGetValue(c.Id, out m) ? m.Reason : null);
        Db.Cmd(db, "DELETE FROM ops.customer_map", tx).ExecuteNonQuery();
        Db.BulkCopy(db, "ops.customer_map", map, tx);
        return merged.Count;
    }

    // One target row per real person. A merged duplicate can still add contact details the survivor lacks.
    public int LoadCustomers(SqlTransaction tx)
    {
        var customers = Db.Shape(db, "target.customers", tx);
        using (var r = Db.Cmd(db, "SELECT c.* FROM legacy.customers c JOIN ops.customer_map m ON m.legacy_cust_id = c.CUST_ID WHERE m.customer_id = c.CUST_ID", tx).ExecuteReader())
            while (r.Read())
            {
                var row = customers.NewRow();
                config.Fill(row, r, "customers", fault);
                if (fault == "flags") row["is_pep"] = row["is_vulnerable"] = false;
                customers.Rows.Add(row);
            }
        using (var r = Db.Cmd(db, "SELECT m.customer_id, c.MOBILE, c.EMAIL FROM legacy.customers c JOIN ops.customer_map m ON m.legacy_cust_id = c.CUST_ID WHERE m.customer_id <> c.CUST_ID", tx).ExecuteReader())
            while (r.Read())
            {
                var survivor = customers.Rows.Find(r.GetString(0))!;
                if (survivor["mobile"] is DBNull) survivor["mobile"] = Rules.Mobile(Rules.Text(r[1])) ?? (object)DBNull.Value;
                if (survivor["email"] is DBNull) survivor["email"] = Rules.Text(r[2]) ?? (object)DBNull.Value;
            }
        Db.BulkCopy(db, "target.customers", customers, tx);
        return customers.Rows.Count;
    }

    // Accounts split into fixed-size cohorts by account number
    public List<(int No, string First, string Last, int Accounts)> PlanCohorts(SqlTransaction? tx = null)
    {
        var numbers = new List<string>();
        using (var r = Db.Cmd(db, "SELECT ACCT_NO FROM legacy.accounts ORDER BY ACCT_NO", tx).ExecuteReader())
            while (r.Read()) numbers.Add(r.GetString(0));
        return numbers.Chunk(config.CohortSize).Select((c, i) => (i + 1, c[0], c[^1], c.Length)).ToList();
    }

    public (int Accounts, int Transactions) LoadCohort(string first, string last, SqlTransaction tx)
    {
        (string, object?)[] range = [("@first", first), ("@last", last)];
        foreach (var table in new[] { "transactions", "account_holders", "accounts" })
            Db.Cmd(db, $"DELETE FROM target.{table} WHERE account_number BETWEEN @first AND @last", tx, range).ExecuteNonQuery();

        // Holders go through the customer map, so a duplicate's accounts land on the surviving customer.
        // The account name is rebuilt from the holders, because the legacy field cut it off at 30 characters.
        var holders = Db.Shape(db, "target.account_holders", tx);
        var names = new Dictionary<string, string>();
        var primaryOnly = fault == "joint-holders" ? " AND h.ROLE = 'PRIMARY'" : "";
        using (var r = Db.Cmd(db, $"""
            SELECT h.ACCT_NO, m.customer_id AS CUST_ID, h.ROLE, UPPER(CONCAT(c.first_name, ' ', c.last_name)) AS NAME
            FROM legacy.account_holders h
            JOIN ops.customer_map m ON m.legacy_cust_id = h.CUST_ID
            JOIN target.customers c ON c.customer_id = m.customer_id
            WHERE h.ACCT_NO BETWEEN @first AND @last{primaryOnly}
            ORDER BY h.ACCT_NO, CASE h.ROLE WHEN 'PRIMARY' THEN 0 ELSE 1 END
            """, tx, range).ExecuteReader())
            while (r.Read())
            {
                var row = holders.NewRow();
                config.Fill(row, r, "account_holders", fault);
                holders.Rows.Add(row);
                var acct = r.GetString(0);
                names[acct] = names.TryGetValue(acct, out var n) ? $"{n} & {r.GetString(3)}" : r.GetString(3);
            }

        var accounts = Db.Shape(db, "target.accounts", tx);
        using (var r = Db.Cmd(db, "SELECT * FROM legacy.accounts WHERE ACCT_NO BETWEEN @first AND @last", tx, range).ExecuteReader())
            while (r.Read())
            {
                var row = accounts.NewRow();
                config.Fill(row, r, "accounts", fault);
                if (names.TryGetValue((string)r["ACCT_NO"], out var name)) row["account_name"] = name;
                if (fault == "rounding") row["balance"] = Math.Round((decimal)row["balance"], 0);
                if (fault == "accrual") row["accrued_interest"] = 0m;
                accounts.Rows.Add(row);
            }

        var transactions = Db.Shape(db, "target.transactions", tx);
        var skipPending = fault == "drop-pending" ? " AND STATUS <> 'PENDING'" : "";
        using (var r = Db.Cmd(db, $"SELECT * FROM legacy.transactions WHERE ACCT_NO BETWEEN @first AND @last{skipPending}", tx, range).ExecuteReader())
            while (r.Read())
            {
                var row = transactions.NewRow();
                config.Fill(row, r, "transactions", fault);
                transactions.Rows.Add(row);
            }

        Db.BulkCopy(db, "target.accounts", accounts, tx);
        Db.BulkCopy(db, "target.account_holders", holders, tx);
        Db.BulkCopy(db, "target.transactions", transactions, tx);
        return (accounts.Rows.Count, transactions.Rows.Count);
    }
}
