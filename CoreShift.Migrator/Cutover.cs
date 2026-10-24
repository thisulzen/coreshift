using Microsoft.Data.SqlClient;

namespace CoreShift.Migrator;

// The cutover as a state machine:
//
//   STARTED → FROZEN → EXTRACTED → TRANSFORMED → LOADED → RECONCILED → GO or NO_GO → COMMITTED or ROLLED_BACK
//
// The state is saved in ops.cutover_runs after every step, and every step is safe to repeat, so a run
// that dies anywhere carries on from where it stopped with `coreshift resume`. A rehearsal is the same
// run except that a GO also ends in rollback: it's a dry run that leaves a result behind.
class Cutover(SqlConnection db, MappingConfig config)
{
    public int Start(string kind, string? fault)
    {
        if (Unfinished() is int open)
            throw new InvalidOperationException($"Run {open} hasn't finished. Carry it on with `coreshift resume`.");
        EnsureTargetNotLive();
        // The check above gives the message; the locks here make it hold when two runs start at the same moment
        var id = Db.Cmd(db, """
            INSERT INTO ops.cutover_runs (kind, state, mapping_version, fault) OUTPUT INSERTED.run_id
            SELECT @kind, 'STARTED', @version, @fault
            WHERE NOT EXISTS (SELECT 1 FROM ops.cutover_runs WITH (UPDLOCK, HOLDLOCK) WHERE state NOT IN ('COMMITTED', 'ROLLED_BACK'))
            """, null, ("@kind", kind), ("@version", config.Version), ("@fault", fault)).ExecuteScalar() as int?
            ?? throw new InvalidOperationException("Another run started at the same moment. Carry it on with `coreshift resume`.");
        Log(id, "START", $"{kind.ToLowerInvariant()} with mapping v{config.Version}{(fault != null ? $" and fault '{fault}'" : "")}");
        return id;
    }

    public int? Unfinished() =>
        Db.Cmd(db, "SELECT TOP 1 run_id FROM ops.cutover_runs WHERE state NOT IN ('COMMITTED', 'ROLLED_BACK') ORDER BY run_id DESC").ExecuteScalar() as int?;

    // Once a cutover commits, the target is the live bank and legacy is retired. Every rehearsal and
    // migrate starts by emptying the target, so none may run until a new legacy bank is loaded.
    public void EnsureTargetNotLive()
    {
        if (Db.Cmd(db, "SELECT c.run_id FROM ops.freeze f JOIN ops.cutover_runs c ON c.run_id = f.frozen_by_run WHERE c.state = 'COMMITTED'").ExecuteScalar() is int live)
            throw new InvalidOperationException($"Cutover {live} committed, so the target is the live bank. Load a new legacy bank with db/load-legacy.sh to rehearse again.");
    }

    // Runs a cutover to the end from whatever state it's in. True means the outcome was GO.
    public bool Run(int runId, int? crashInCohort = null)
    {
        while (true)
        {
            string state, kind; string? fault;
            using (var r = Db.Cmd(db, "SELECT state, kind, fault FROM ops.cutover_runs WHERE run_id = @run", null, ("@run", runId)).ExecuteReader())
            {
                r.Read();
                (state, kind, fault) = (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
            }
            var migration = new Migration(db, config, fault);
            switch (state)
            {
                case "STARTED":
                    Exec(runId, "UPDATE ops.freeze SET frozen_by_run = @run");
                    Next(runId, "FROZEN", "legacy is frozen: its tables now refuse every write");
                    break;
                case "FROZEN":
                    Exec(runId, "UPDATE ops.cutover_runs SET legacy_fingerprint = (SELECT fingerprint FROM ops.legacy_fingerprint) WHERE run_id = @run");
                    Log(runId, "SCAM WATCH", $"{Db.Cmd(db, "SELECT COUNT(*) FROM ops.scam_watch").ExecuteScalar()} accounts sent more than they kept to a new payee in the week before the freeze; for the fraud team (coreshift scam-watch)");
                    Next(runId, "EXTRACTED", "legacy fingerprint recorded: row counts and highest rowversion per table");
                    break;
                case "EXTRACTED":
                {
                    using var tx = db.BeginTransaction();
                    var merged = migration.BuildCustomerMap(tx);
                    tx.Commit();
                    Next(runId, "TRANSFORMED", $"customer map built: {merged} duplicates to merge");
                    break;
                }
                case "TRANSFORMED":
                    Load(runId, migration, crashInCohort);
                    Next(runId, "LOADED", "every cohort loaded");
                    break;
                case "LOADED":
                {
                    var rehearsal = Reconciler.Run(db, config.Version, fault, runId);
                    Exec(runId, "UPDATE ops.cutover_runs SET rehearsal_id = @rehearsal WHERE run_id = @run", ("@rehearsal", rehearsal));
                    Next(runId, "RECONCILED", $"reconciliation filed as rehearsal {rehearsal}");
                    break;
                }
                case "RECONCILED":
                    Decide(runId);
                    break;
                case "GO" when kind == "CUTOVER":
                    Next(runId, "COMMITTED", "target is live; legacy stays frozen and retired");
                    break;
                case "GO" or "NO_GO":
                    Rollback(runId, migration);
                    break;
                default:   // COMMITTED or ROLLED_BACK
                    return (string?)Db.Cmd(db, "SELECT outcome FROM ops.cutover_runs WHERE run_id = @run", null, ("@run", runId)).ExecuteScalar() == "GO";
            }
        }
    }

    // First time in: empty the target and load customers, together with the cohort plan, in one
    // transaction. Then each cohort commits on its own, together with its "loaded" mark.
    void Load(int runId, Migration migration, int? crashInCohort)
    {
        if (!(bool)Db.Cmd(db, "SELECT customers_loaded FROM ops.cutover_runs WHERE run_id = @run", null, ("@run", runId)).ExecuteScalar()!)
        {
            using var tx = db.BeginTransaction();
            migration.ClearTarget(tx);
            var customers = migration.LoadCustomers(tx);
            var cohorts = migration.PlanCohorts(tx);
            foreach (var (no, first, last, accounts) in cohorts)
                Db.Cmd(db, "INSERT INTO ops.cohorts (run_id, cohort_no, first_account, last_account, accounts) VALUES (@run, @no, @first, @last, @n)",
                    tx, ("@run", runId), ("@no", no), ("@first", first), ("@last", last), ("@n", accounts)).ExecuteNonQuery();
            Db.Cmd(db, "UPDATE ops.cutover_runs SET customers_loaded = 1 WHERE run_id = @run", tx, ("@run", runId)).ExecuteNonQuery();
            tx.Commit();
            Log(runId, "LOAD", $"target emptied, {customers:N0} customers loaded, {cohorts.Count} cohorts planned");
        }

        var pending = new List<(int No, string First, string Last)>();
        using (var r = Db.Cmd(db, "SELECT cohort_no, first_account, last_account FROM ops.cohorts WHERE run_id = @run AND loaded_at IS NULL ORDER BY cohort_no", null, ("@run", runId)).ExecuteReader())
            while (r.Read()) pending.Add((r.GetInt32(0), r.GetString(1), r.GetString(2)));
        foreach (var (no, first, last) in pending)
        {
            using var tx = db.BeginTransaction();
            var (accounts, transactions) = migration.LoadCohort(first, last, tx);
            if (no == crashInCohort)
            {
                // Dies with the cohort's transaction still open; SQL Server rolls it back
                Console.WriteLine($"  CRASH      simulated crash inside cohort {no}, before it commits");
                Environment.Exit(70);
            }
            Db.Cmd(db, "UPDATE ops.cohorts SET loaded_at = SYSUTCDATETIME() WHERE run_id = @run AND cohort_no = @no", tx, ("@run", runId), ("@no", no)).ExecuteNonQuery();
            tx.Commit();
            Log(runId, "LOAD", $"cohort {no}: {accounts:N0} accounts, {transactions:N0} transactions");
        }
    }

    void Decide(int runId)
    {
        bool passed; int unexplained;
        using (var r = Db.Cmd(db, "SELECT r.passed, r.unexplained FROM ops.rehearsals r JOIN ops.cutover_runs c ON c.rehearsal_id = r.rehearsal_id WHERE c.run_id = @run", null, ("@run", runId)).ExecuteReader())
        {
            r.Read();
            (passed, unexplained) = (r.GetBoolean(0), r.GetInt32(1));
        }
        var outcome = passed ? "GO" : "NO_GO";
        Exec(runId, "UPDATE ops.cutover_runs SET outcome = @outcome WHERE run_id = @run", ("@outcome", outcome));
        Next(runId, outcome, passed ? "go: no unexplained breaks, totals agree, freeze held" : $"no-go: {unexplained:N0} unexplained breaks");
    }

    // Everything the run loaded goes, and legacy is writable again, in one transaction
    void Rollback(int runId, Migration migration)
    {
        using var tx = db.BeginTransaction();
        migration.ClearTarget(tx);
        Db.Cmd(db, "DELETE FROM ops.customer_map; UPDATE ops.freeze SET frozen_by_run = NULL", tx).ExecuteNonQuery();
        Db.Cmd(db, "UPDATE ops.cutover_runs SET state = 'ROLLED_BACK', updated_at = SYSUTCDATETIME() WHERE run_id = @run", tx, ("@run", runId)).ExecuteNonQuery();
        tx.Commit();
        Log(runId, "ROLLED_BACK", "target emptied, legacy unfrozen");
    }

    void Next(int runId, string state, string message)
    {
        Exec(runId, "UPDATE ops.cutover_runs SET state = @state, updated_at = SYSUTCDATETIME() WHERE run_id = @run", ("@state", state));
        Log(runId, state, message);
    }

    void Exec(int runId, string sql, params (string, object?)[] args) =>
        Db.Cmd(db, sql, null, [("@run", runId), .. args]).ExecuteNonQuery();

    // The audit log: every step, in the database and on screen
    void Log(int runId, string step, string message)
    {
        Db.Cmd(db, "INSERT INTO ops.cutover_log (run_id, step, message) VALUES (@run, @step, @message)", null, ("@run", runId), ("@step", step), ("@message", message)).ExecuteNonQuery();
        Console.WriteLine($"  {step,-12} {message}");
    }
}
