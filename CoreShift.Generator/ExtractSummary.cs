namespace CoreShift.Generator;

// Adds up everything the console summary and control totals need, one household at a time,
// so no rows have to stay in memory once they're written.
public class ExtractSummary
{
    class ProductTotals
    {
        public string Name = "";
        public int Open, Closed, Other;
        public decimal Balance;
    }

    readonly SortedDictionary<string, ProductTotals> products = new();
    long customers, accounts, holders, transactions, pending;
    decimal income, deposits, owed, transactionTotal, accrued;
    int breaks, subCent, messyNames, oldCustomerDates, oldAccountDates, bsbNoDash, oddMobiles,
        transactionAccounts, overdrawn, arrears, hardship, preAml;

    public bool Passed => breaks == 0 && subCent == 0;

    public void Add(List<LegacyCustomer> cs, List<LegacyAccount> accts, List<LegacyAccountHolder> hs, List<LegacyTransaction> txns)
    {
        customers += cs.Count;
        accounts += accts.Count;
        holders += hs.Count;
        transactions += txns.Count;
        pending += txns.Count(t => t.Status == "PENDING");
        income += cs.Sum(c => c.AnnualIncome);
        transactionTotal += txns.Sum(t => t.Amount);

        foreach (var a in accts)
        {
            if (!products.TryGetValue(a.ProductCode, out var p))
                products[a.ProductCode] = p = new ProductTotals { Name = a.ProductName };
            p.Balance += a.Balance;
            switch (a.Status)
            {
                case "OPEN": p.Open++; break;
                case "CLOSED": p.Closed++; break;
                default: p.Other++; break;
            }
        }
        deposits += accts.Where(a => a.Balance > 0).Sum(a => a.Balance);
        owed += accts.Where(a => a.Balance < 0).Sum(a => a.Balance);
        accrued += accts.Sum(a => a.AccruedInterest);

        // Every balance must equal the sum of its posted history, and no amount may carry
        // fractions of a cent (the CSV would quietly round them away). A household's accounts
        // and transactions arrive together, so this can be checked batch by batch.
        var posted = txns.Where(t => t.Status == "POSTED").GroupBy(t => t.AcctNo).ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
        breaks += accts.Count(a => a.Balance != posted.GetValueOrDefault(a.AcctNo));
        subCent += txns.Count(t => t.Amount != Math.Round(t.Amount, 2));

        messyNames += cs.Count(c => c.FirstName != c.FirstName.Trim() || c.FirstName == c.FirstName.ToLowerInvariant());
        oldCustomerDates += cs.Count(c => !c.DateOfBirth.Contains('/'));
        oldAccountDates += accts.Count(a => !a.OpenDate.Contains('/'));
        bsbNoDash += accts.Count(a => !a.Bsb.Contains('-'));
        oddMobiles += cs.Count(c => c.Mobile?.StartsWith("+61") == true || c.Mobile?.Contains(' ') == false);
        var transactionAccts = accts.Where(a => a.ProductCode is "EVR00" or "EVR01" or "EVR02" or "OFF01" && a.Status != "CLOSED").ToList();
        transactionAccounts += transactionAccts.Count;
        overdrawn += transactionAccts.Count(a => a.Balance < 0);
        arrears += accts.Count(a => a.ArrearsDays > 0);
        hardship += accts.Count(a => a.Hardship);
        preAml += cs.Count(c => c.KycStatus == "PRE_COMMENCEMENT");
    }

    // Like the trailer record on a real extract: the load has to reproduce these exactly
    public string[] ControlTotals() =>
    [
        "TABLE_NAME,ROW_COUNT,AMOUNT_TOTAL",
        $"customers,{customers},{income:0.00}",
        $"accounts,{accounts},{deposits + owed:0.00}",
        $"account_holders,{holders},",
        $"transactions,{transactions},{transactionTotal:0.00}",
    ];

    public void Print(DateOnly extractDate, int seed, int duplicates, int scams, string outDir)
    {
        Console.WriteLine($"Legacy core extract as at {extractDate:dd/MM/yyyy} (seed {seed}), synthetic data only");
        Console.WriteLine($"  {customers:N0} customers, {accounts:N0} accounts, {transactions:N0} transactions");
        Console.WriteLine();
        Console.WriteLine($"  {"Product",-26}{"Open",6}{"Closed",8}{"Dormant/Frozen",16}{"Balance",18}");
        foreach (var p in products.Values)
            Console.WriteLine($"  {p.Name,-26}{p.Open,6}{p.Closed,8}{p.Other,16}{p.Balance,18:N2}");
        Console.WriteLine($"  {"Deposits held",-56}{deposits,18:N2}");
        Console.WriteLine($"  {"Owed to the bank",-56}{owed,18:N2}");
        Console.WriteLine();
        Console.WriteLine(Passed
            ? "  Self-check: every balance equals the sum of its posted transactions, all amounts in whole cents"
            : $"  Self-check FAILED: {breaks} balances don't match their transactions, {subCent} amounts have fractions of a cent");
        Console.WriteLine();
        Console.WriteLine("  Legacy quirks the migration will have to handle:");
        Console.WriteLine($"    {duplicates} duplicate customer records");
        Console.WriteLine($"    {messyNames} names lowercase or padded with spaces");
        Console.WriteLine($"    {oldCustomerDates} customers and {oldAccountDates} accounts with yyyyMMdd dates from before the 2010 conversion");
        Console.WriteLine($"    {bsbNoDash} BSBs stored without the dash, {oddMobiles} mobiles not in 04xx xxx xxx form");
        Console.WriteLine($"    {overdrawn} of {transactionAccounts} transaction accounts overdrawn");
        Console.WriteLine($"    {arrears} loans/cards in arrears, {hardship} in hardship");
        Console.WriteLine($"    {pending} pending card transactions, {accrued:N2} net accrued interest unposted");
        Console.WriteLine($"    {preAml} pre-AML customers never re-identified");
        Console.WriteLine($"    {scams} accounts emptied by a scam call on the extract day");
        Console.WriteLine();
        Console.WriteLine($"  Files: {outDir}");
    }
}
