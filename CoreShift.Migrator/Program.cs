using CoreShift.Migrator;
using Microsoft.Data.SqlClient;

const string Usage = """
    Usage: coreshift <command> [options]
      migrate   [--fault F]   load the target bank from legacy; safe to run again
      reconcile               compare legacy with target; exits 1 on any unexplained break
    Options:
      --fault F        inject a deliberate mapping bug: rounding, drop-pending, date-format, accrual
      --mapping PATH   use a different mapping file (default: mapping.v1.json next to the tool)
    """;

string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var command = args.FirstOrDefault();
var fault = Option("--fault");
if (command is not ("migrate" or "reconcile") || (fault != null && !Migration.Faults.Contains(fault)))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var config = MappingConfig.Load(Option("--mapping") ?? Path.Combine(AppContext.BaseDirectory, "mapping.v1.json"));
try
{
    using var db = Db.Open();
    Db.RunScript(db, Db.Resource("target-schema.sql"));
    if (command == "reconcile")
    {
        // Filed under the mapping version and fault the target was actually loaded with
        using var r = Db.Cmd(db, "SELECT mapping_version, fault FROM ops.target_load").ExecuteReader();
        var (version, loadedFault) = r.Read() ? (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)) : (config.Version, null);
        r.Close();
        return Reconciler.Report(db, Reconciler.Run(db, version, loadedFault)) ? 0 : 1;
    }
    var migration = new Migration(db, config, fault);

    Console.WriteLine($"Migrating with mapping v{config.Version}{(fault != null ? $", fault: {fault}" : "")}");
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
    return 0;
}
catch (Exception e) when (e is SqlException or InvalidDataException or InvalidOperationException)
{
    Console.Error.WriteLine($"Failed: {e.Message}");
    return 1;
}
