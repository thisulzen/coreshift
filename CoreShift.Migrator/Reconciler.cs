using Microsoft.Data.SqlClient;

namespace CoreShift.Migrator;

static class Reconciler
{
    // Runs db/06-reconcile.sql and returns the rehearsal id its results are filed under
    public static int Run(SqlConnection db, string mappingVersion, string? fault, int? runId = null)
    {
        var id = (int)Db.Cmd(db, "INSERT INTO ops.rehearsals (run_id, mapping_version, fault) OUTPUT INSERTED.rehearsal_id VALUES (@run, @version, @fault)",
            null, ("@run", runId), ("@version", mappingVersion), ("@fault", fault)).ExecuteScalar()!;
        Db.Cmd(db, Db.Resource("reconcile.sql"), null, ("@rehearsal_id", id)).ExecuteNonQuery();
        return id;
    }

    // The break report: totals side by side, then breaks grouped by reason with a few examples each
    public static bool Report(SqlConnection db, int id)
    {
        using (var r = Db.Cmd(db, "SELECT * FROM ops.rehearsals WHERE rehearsal_id = @id", null, ("@id", id)).ExecuteReader())
        {
            r.Read();
            Console.WriteLine($"Reconciliation (rehearsal {id})");
            Console.WriteLine($"  {"",-14}{"legacy",20}{"target",20}");
            Console.WriteLine($"  {"Customers",-14}{r["legacy_customers"],20:N0}{r["target_customers"],20:N0}   {r["merged_customers"]} duplicates merged");
            Console.WriteLine($"  {"Accounts",-14}{r["legacy_accounts"],20:N0}{r["target_accounts"],20:N0}");
            Console.WriteLine($"  {"Transactions",-14}{r["legacy_transactions"],20:N0}{r["target_transactions"],20:N0}");
            Console.WriteLine($"  {"Balance",-14}{r["legacy_balance"],20:N2}{r["target_balance"],20:N2}");
            Console.WriteLine($"  Breaks: {r["breaks"]:N0}, of which {r["unexplained"]:N0} unexplained");
        }
        using (var r = Db.Cmd(db, """
            SELECT reason, explained, COUNT(*) AS n,
                   STRING_AGG(CASE WHEN k <= 3 THEN CAST(entity_key AS varchar(max)) END, ', ') WITHIN GROUP (ORDER BY entity_key) AS examples
            FROM (SELECT reason, explained, entity_key, ROW_NUMBER() OVER (PARTITION BY reason ORDER BY entity_key) AS k
                  FROM ops.breaks WHERE rehearsal_id = @id) b
            GROUP BY reason, explained
            ORDER BY explained, n DESC
            """, null, ("@id", id)).ExecuteReader())
            while (r.Read())
                Console.WriteLine($"  {r.GetInt32(2),8:N0}  {(r.GetBoolean(1) ? "explained  " : "UNEXPLAINED")}  {r.GetString(0)}  (e.g. {r.GetString(3)})");
        var passed = (bool)Db.Cmd(db, "SELECT passed FROM ops.rehearsals WHERE rehearsal_id = @id", null, ("@id", id)).ExecuteScalar()!;
        Console.WriteLine(passed ? "  Result: PASS" : "  Result: FAIL");
        return passed;
    }
}
