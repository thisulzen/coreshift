using System.Globalization;
using Bogus;
using CoreShift.Generator;

int customerCount = 1_000, seed = 42;
if (args.Length > 2
    || (args.Length > 0 && (!int.TryParse(args[0], out customerCount) || customerCount < 1))
    || (args.Length > 1 && !int.TryParse(args[1], out seed)))
{
    Console.Error.WriteLine("Usage: dotnet run -- [customers (at least 1)] [seed]");
    return 2;
}

// End of day on this date the legacy core is frozen and extracted. It's mid-month on purpose:
// savings interest has accrued since the 1st but not been paid, and the migration has to carry it.
var extractDate = new DateOnly(2026, 9, 16);

// Same seed and date = the same bank every run, so rehearsals compare like with like
Randomizer.Seed = new Random(seed);
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var outDir = Path.GetFullPath(Path.Combine("output", "legacy"));
Directory.CreateDirectory(outDir);
var summary = new ExtractSummary();
var bank = new LegacyBankGenerator(extractDate, historyMonths: 3);

using (var customers = new CsvFile<LegacyCustomer>(Path.Combine(outDir, "customers.csv")))
using (var accounts = new CsvFile<LegacyAccount>(Path.Combine(outDir, "accounts.csv")))
using (var holders = new CsvFile<LegacyAccountHolder>(Path.Combine(outDir, "account_holders.csv")))
using (var transactions = new CsvFile<LegacyTransaction>(Path.Combine(outDir, "transactions.csv")))
{
    bank.Generate(customerCount, () =>
    {
        customers.WriteAll(bank.Customers);
        accounts.WriteAll(bank.Accounts);
        holders.WriteAll(bank.Holders);
        transactions.WriteAll(bank.Transactions);
        summary.Add(bank.Customers, bank.Accounts, bank.Holders, bank.Transactions);
    });
}
File.WriteAllLines(Path.Combine(outDir, "control_totals.csv"), summary.ControlTotals());

summary.Print(extractDate, seed, bank.DuplicatesSeeded, outDir);
return summary.Passed ? 0 : 1;
