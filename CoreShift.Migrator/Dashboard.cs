using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CoreShift.Migrator;

// Rehearsal history in the browser: pass rate, breaks by reason, the trend across rehearsals and
// the latest run's audit log. One HTML page that reads three small JSON endpoints.
static class Dashboard
{
    public static void Run(int port)
    {
        var app = WebApplication.Create();
        app.MapGet("/", () => Results.Content(Db.Resource("dashboard.html"), "text/html"));
        app.MapGet("/api/rehearsals", () => Query("""
            SELECT r.rehearsal_id, r.started_at, ISNULL(c.kind, 'RECONCILE') AS kind, r.fault, r.mapping_version,
                   r.legacy_accounts, r.target_accounts, r.legacy_transactions, r.target_transactions,
                   r.legacy_balance, r.target_balance, r.merged_customers, r.breaks, r.unexplained, r.passed
            FROM ops.rehearsals r LEFT JOIN ops.cutover_runs c ON c.run_id = r.run_id
            WHERE r.passed IS NOT NULL ORDER BY r.rehearsal_id
            """));
        app.MapGet("/api/rehearsals/{id:int}/breaks", (int id) => Query("""
            SELECT reason, explained, COUNT(*) AS breaks,
                   MAX(CASE WHEN k = 1 THEN entity_key END) AS example, MAX(CASE WHEN k = 1 THEN detail END) AS example_detail
            FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY reason ORDER BY entity_key) AS k FROM ops.breaks WHERE rehearsal_id = @id) b
            GROUP BY reason, explained ORDER BY explained, COUNT(*) DESC
            """, ("@id", id)));
        app.MapGet("/api/runs/latest", () => Query("""
            SELECT l.at, l.step, l.message, c.run_id, c.kind, c.state, c.outcome
            FROM ops.cutover_log l JOIN ops.cutover_runs c ON c.run_id = l.run_id
            WHERE l.run_id = (SELECT MAX(run_id) FROM ops.cutover_runs) ORDER BY l.log_id
            """));
        Console.WriteLine($"Dashboard on http://localhost:{port} (Ctrl+C to stop)");
        app.Run($"http://localhost:{port}");
    }

    static IResult Query(string sql, params (string, object?)[] args)
    {
        using var db = Db.Open();
        using var r = Db.Cmd(db, sql, null, args).ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (r.Read())
            rows.Add(Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => r.IsDBNull(i) ? null : r.GetValue(i)));
        return Results.Json(rows);
    }
}
