using System.Globalization;
using Bogus;
using CoreShift.Generator;

// Usage: dotnet run -- [customers] [seed]
var customerCount = args.Length > 0 ? int.Parse(args[0]) : 1_000;
var seed = args.Length > 1 ? int.Parse(args[1]) : 42;

// End of day on this date the legacy core is frozen and extracted. It's mid-month on purpose:
// savings interest has accrued since the 1st but not been paid, and the migration has to carry it.
var extractDate = new DateOnly(2026, 9, 16);

// Same seed and date = the same bank every run, so rehearsals compare like with like
Randomizer.Seed = new Random(seed);
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var bank = new LegacyBankGenerator(extractDate, historyMonths: 3);
bank.Generate(customerCount);

var outDir = Path.GetFullPath(Path.Combine("output", "legacy"));
Directory.CreateDirectory(outDir);
Csv.Write(Path.Combine(outDir, "customers.csv"), bank.Customers);
Csv.Write(Path.Combine(outDir, "accounts.csv"), bank.Accounts);
Csv.Write(Path.Combine(outDir, "account_holders.csv"), bank.Holders);
Csv.Write(Path.Combine(outDir, "transactions.csv"), bank.Transactions);

var accounts = bank.Accounts;
var posted = bank.Transactions.Where(t => t.Status == "POSTED").ToList();

Console.WriteLine($"Legacy core extract as at {extractDate:dd/MM/yyyy} (seed {seed}), synthetic data only");
Console.WriteLine($"  {bank.Customers.Count:N0} customers, {accounts.Count:N0} accounts, {bank.Transactions.Count:N0} transactions");
Console.WriteLine();
Console.WriteLine($"  {"Product",-26}{"Open",6}{"Closed",8}{"Dormant/Frozen",16}{"Balance",18}");
foreach (var g in accounts.GroupBy(a => a.ProductCode).OrderBy(g => g.Key))
    Console.WriteLine($"  {g.First().ProductName,-26}{g.Count(a => a.Status == "OPEN"),6}{g.Count(a => a.Status == "CLOSED"),8}" +
                      $"{g.Count(a => a.Status is "DORMANT" or "FROZEN"),16}{g.Sum(a => a.Balance),18:N2}");
Console.WriteLine($"  {"Deposits held",-56}{accounts.Where(a => a.Balance > 0).Sum(a => a.Balance),18:N2}");
Console.WriteLine($"  {"Owed to the bank",-56}{accounts.Where(a => a.Balance < 0).Sum(a => a.Balance),18:N2}");

// A first taste of Stage 4: every balance must equal the sum of its posted history
var sums = posted.GroupBy(t => t.AcctNo).ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
var breaks = accounts.Count(a => a.Balance != sums.GetValueOrDefault(a.AcctNo));
Console.WriteLine();
Console.WriteLine(breaks == 0
    ? "  Self-check: every account balance equals the sum of its posted transactions"
    : $"  Self-check FAILED: {breaks} account balances don't match their transactions");

var transactionAccounts = accounts.Where(a => a.ProductCode is "EVR00" or "EVR01" or "EVR02" or "OFF01" && a.Status != "CLOSED").ToList();
Console.WriteLine();
Console.WriteLine("  Legacy quirks the migration will have to handle:");
Console.WriteLine($"    {bank.DuplicatesSeeded} duplicate customer records");
Console.WriteLine($"    {bank.Customers.Count(c => c.FirstName != c.FirstName.Trim() || c.FirstName == c.FirstName.ToLowerInvariant())} names lowercase or padded with spaces");
Console.WriteLine($"    {bank.Customers.Count(c => !c.DateOfBirth.Contains('/'))} customers and {accounts.Count(a => !a.OpenDate.Contains('/'))} accounts with yyyyMMdd dates from before the 2010 conversion");
Console.WriteLine($"    {accounts.Count(a => !a.Bsb.Contains('-'))} BSBs stored without the dash, {bank.Customers.Count(c => c.Mobile?.StartsWith("+61") == true || c.Mobile?.Contains(' ') == false)} mobiles not in 04xx xxx xxx form");
Console.WriteLine($"    {transactionAccounts.Count(a => a.Balance < 0)} of {transactionAccounts.Count} transaction accounts overdrawn");
Console.WriteLine($"    {accounts.Count(a => a.ArrearsDays > 0)} loans/cards in arrears, {accounts.Count(a => a.Hardship)} in hardship");
Console.WriteLine($"    {bank.Transactions.Count - posted.Count} pending card transactions, {accounts.Sum(a => a.AccruedInterest):N2} net accrued interest unposted");
Console.WriteLine($"    {bank.Customers.Count(c => c.KycStatus == "PRE_COMMENCEMENT")} pre-AML customers never re-identified");
Console.WriteLine();
Console.WriteLine($"  Files: {outDir}");
