using CoreShift.Migrator;
using Microsoft.Data.SqlClient;

var usage = $"""
    Usage: coreshift <command> [options]
      migrate   [--fault F]   load the target bank from legacy; safe to run again
      reconcile               compare legacy with target; exits 1 on any unexplained break
      rehearse  [--fault F]   a full cutover as a dry run: always ends in rollback
      cutover   [--fault F]   the real thing: commit on go, roll back on no-go
      resume                  carry on a rehearsal or cutover that stopped part-way
      dashboard [--port P]    rehearsal history in the browser (default port 5080)
      scam-watch              accounts emptied to a new payee in the week before the extract, for the fraud team
    Options:
      --fault F            inject a deliberate mapping bug: {string.Join(", ", Migration.Faults)}
      --crash-in-cohort N  (rehearse, cutover, resume) die inside cohort N before it commits, to test recovery
      --mapping PATH       use a different mapping file (default: mapping.v1.json next to the tool)
    Exit codes: 0 pass or go, 1 fail or no-go, 2 usage
    """;

string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var command = args.FirstOrDefault();
var fault = Option("--fault");
int? crashInCohort = int.TryParse(Option("--crash-in-cohort"), out var n) ? n : null;
if (command is not ("migrate" or "reconcile" or "rehearse" or "cutover" or "resume" or "dashboard" or "scam-watch") || (fault != null && !Migration.Faults.Contains(fault)))
{
    Console.Error.WriteLine(usage);
    return 2;
}

try
{
    var config = MappingConfig.Load(Option("--mapping") ?? Path.Combine(AppContext.BaseDirectory, "mapping.v1.json"));
    using var db = Db.Open();
    Db.RunScript(db, Db.Resource("target-schema.sql"));
    var cutover = new Cutover(db, config);

    switch (command)
    {
        case "dashboard":
            Dashboard.Run(int.TryParse(Option("--port"), out var port) ? port : 5080);
            return 0;

        case "migrate":
            if (cutover.Unfinished() is int running)
                throw new InvalidOperationException($"Run {running} owns the target until it finishes. Carry it on with `coreshift resume`.");
            cutover.EnsureTargetNotLive();
            Migrate(db, new Migration(db, config, fault));
            return 0;

        case "reconcile":
        {
            // Filed under the mapping version and fault the target was actually loaded with
            using var r = Db.Cmd(db, "SELECT mapping_version, fault FROM ops.target_load").ExecuteReader();
            var (version, loadedFault) = r.Read() ? (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)) : (config.Version, null);
            r.Close();
            return Reconciler.Report(db, Reconciler.Run(db, version, loadedFault)) ? 0 : 1;
        }

        case "scam-watch":
        {
            using var r = Db.Cmd(db, "SELECT account_number, sent, balance_left, last_paid, payees FROM ops.scam_watch ORDER BY sent DESC").ExecuteReader();
            Console.WriteLine($"{"Account",-10}{"Sent",13}{"Left",13}  {"Last paid",-10}  New payee");
            while (r.Read())
                Console.WriteLine($"{r.GetString(0),-10}{r.GetDecimal(1),13:N2}{r.GetDecimal(2),13:N2}  {r.GetDateTime(3):dd/MM/yyyy}  {r.GetString(4)}");
            return 0;
        }

        case "rehearse" or "cutover":
        {
            var runId = cutover.Start(command == "cutover" ? "CUTOVER" : "REHEARSAL", fault);
            return Finish(runId, cutover.Run(runId, crashInCohort));
        }

        default:   // resume
            if (cutover.Unfinished() is not int open)
            {
                Console.WriteLine("Nothing to resume: every run has finished.");
                return 0;
            }
            Console.WriteLine($"Resuming run {open}");
            return Finish(open, cutover.Run(open, crashInCohort));
    }

    int Finish(int runId, bool go)
    {
        var rehearsal = (int)Db.Cmd(db, "SELECT rehearsal_id FROM ops.cutover_runs WHERE run_id = @run", null, ("@run", runId)).ExecuteScalar()!;
        Console.WriteLine();
        Reconciler.Report(db, rehearsal);
        return go ? 0 : 1;
    }
}
catch (Exception e) when (e is SqlException or InvalidDataException or InvalidOperationException or IOException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"Failed: {e.Message}");
    return 1;
}

static void Migrate(SqlConnection db, Migration migration)
{
    using (var tx = db.BeginTransaction())
    {
        migration.ClearTarget(tx);
        var merged = migration.BuildCustomerMap(tx);
        var customers = migration.LoadCustomers(tx);
        tx.Commit();
        Console.WriteLine($"  {customers:N0} customers ({merged} duplicates merged)");
    }
    var cohorts = migration.PlanCohorts();
    int accounts = 0, transactions = 0;
    foreach (var (_, first, last, _) in cohorts)
    {
        using var tx = db.BeginTransaction();
        var (a, t) = migration.LoadCohort(first, last, tx);
        tx.Commit();
        (accounts, transactions) = (accounts + a, transactions + t);
    }
    Console.WriteLine($"  {accounts:N0} accounts and {transactions:N0} transactions in {cohorts.Count} cohorts");
}
