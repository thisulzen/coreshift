using System.Globalization;
using Bogus;
using Bogus.DataSets;

namespace CoreShift.Generator;

// Builds a synthetic legacy bank: households of customers, their accounts, and a few months of
// day-by-day banking. Every account's history opens with its brought-forward balance and each
// posted transaction moves it, so every balance in the extract is exactly the sum of its history.
public class LegacyBankGenerator
{
    enum Kind { Everyday, Offset, Savings, TermDeposit, HomeLoan, PersonalLoan, CreditCard }

    record Branch(string Code, string State, string Bsb);
    record Address(string Line, string Suburb, string State, string Postcode);
    record Bill(string Code, string Description, decimal Amount, int Day, int EveryMonths, int MonthOffset, bool OnCard);

    class Person
    {
        public required LegacyCustomer Row;
        public required string First, Last;
        public DateOnly Dob, Since;
        public bool OldCore;
        public int Age;
        public decimal Income, Pay;
        public string PayDescription = "";
        public bool PaidMonthly, CasualHours;
        public DateOnly PayAnchor;
    }

    class Acct
    {
        public required LegacyAccount Row;
        public List<Person> Owners = [];
        public Kind Kind;
        public DateOnly Opened;
        public bool OldCore;
        public string Tfn = "";               // primary holder's TFN status, for withholding tax
        public decimal Opening;               // balance when the window opens, or the first deposit/drawdown if opened later
        public decimal Accrued;               // interest since the last posting, unrounded
        public decimal Pending;               // card authorisations not yet posted
        public decimal DepositsThisMonth;
        public DateOnly? LastActivity, LastInterest, TermStart;
        // Loans and cards
        public int DueDay;                    // loans: repayment day of month; cards: statement day
        public DateOnly? FortnightAnchor;
        public decimal Extra;                 // voluntary extra repayment
        public bool PaysInFull;
        public decimal StatementOwed, MinDue, Arrears, CycleSpend, LastCycleSpend;
        public DateOnly? CardDue, MissFrom, FirstMissed;
        public double SlipChance;             // odds of a late month in the history before the window
    }

    class Household
    {
        public List<Person> People = [];
        public List<Acct> Accounts = [];
        public Acct? Main, Savings, Card, Offset;
        public List<Bill> Bills = [];
        public string Suburb = "", RentPayee = "";
        public string[] Grocers = [], Eateries = [];
        public decimal Rent, DailySpend, SaveRate;
        public DateOnly RentAnchor;
        public bool Frozen;
        public double Quality;                // credit quality: drives scores, defaults and arrears
    }

    // Records keyed in before the 2010 core conversion kept the old mainframe date format
    static readonly DateOnly CoreConversion = new(2010, 1, 1);
    // Customers who joined before this are "pre-commencement customers" under the AML/CTF Act
    static readonly DateOnly AmlCommencement = new(2007, 12, 12);
    static readonly DateOnly BankFounded = new(1985, 7, 1);

    // A regional bank's book leans towards its home states. The third BSB digit encodes the state.
    static readonly (string State, double Weight, char BsbDigit, int MedianHome)[] States =
    [
        ("QLD", 0.40, '4', 780_000), ("SA", 0.25, '5', 720_000), ("NSW", 0.12, '2', 1_150_000),
        ("VIC", 0.09, '3', 820_000), ("WA", 0.05, '6', 760_000), ("NT", 0.04, '5', 520_000),
        ("TAS", 0.03, '7', 650_000), ("ACT", 0.02, '2', 950_000),
    ];

    // Occupation and its typical full-time salary
    static readonly (string Name, int Median)[] Occupations =
    [
        ("Registered Nurse", 98_000), ("Teacher", 105_000), ("Electrician", 100_000), ("Retail Assistant", 58_000),
        ("Truck Driver", 80_000), ("Accountant", 95_000), ("Software Developer", 130_000), ("Chef", 65_000),
        ("Farm Hand", 60_000), ("Mine Worker", 140_000), ("Carpenter", 85_000), ("Administration Officer", 70_000),
        ("Police Officer", 100_000), ("Aged Care Worker", 62_000), ("Plumber", 95_000), ("Sales Representative", 85_000),
        ("Mechanic", 78_000), ("Cleaner", 55_000), ("Public Servant", 95_000), ("Civil Engineer", 125_000),
        ("Pharmacist", 100_000), ("Hairdresser", 55_000), ("Labourer", 65_000), ("Real Estate Agent", 90_000),
        ("Childcare Worker", 60_000), ("Project Manager", 135_000), ("Warehouse Worker", 62_000), ("Paramedic", 100_000),
        ("Dental Assistant", 58_000), ("Barista", 52_000),
    ];

    // Self-employed: occupation, how the business name ends, typical drawings
    static readonly (string Name, string Business, int Median)[] Trades =
    [
        ("Plumber", "PLUMBING", 110_000), ("Electrician", "ELECTRICAL", 110_000), ("Accountant", "ACCOUNTING", 120_000),
        ("Carpenter", "CARPENTRY", 95_000), ("Truck Driver", "TRANSPORT", 90_000), ("Graphic Designer", "DESIGN", 75_000),
        ("Hairdresser", "HAIR STUDIO", 60_000), ("Farmer", "PASTORAL CO", 80_000), ("Consultant", "CONSULTING", 130_000),
        ("Cafe Owner", "CAFE", 65_000),
    ];

    // Where discretionary money goes: share of the budget and how often it happens
    static readonly (string Category, decimal Share, double EveryDays)[] SpendCategories =
    [
        ("GROCERIES", 0.34m, 3.5), ("FUEL", 0.12m, 7), ("DINING", 0.16m, 2.5), ("SHOPPING", 0.18m, 9),
        ("HEALTH", 0.05m, 20), ("CASH", 0.05m, 18), ("PEOPLE", 0.10m, 10),
    ];

    readonly Faker f = new("en_AU");
    readonly DateOnly extractDate, historyStart;
    readonly List<Branch> branches = [];
    readonly Branch digital = new("BR999", "QLD", "994-999");
    readonly string[] employers, superFunds, grocers, fuel, pharmacies, online, telcos, broadband,
        insurers, healthFunds, energy, water, streaming, gyms, agents;
    int nextCustId = 1_000_000, nextTxnId;
    long nextAcctNo = 100_000_000;

    // The current household's rows, ready for Generate's flush
    public List<LegacyCustomer> Customers { get; } = [];
    public List<LegacyAccount> Accounts { get; } = [];
    public List<LegacyAccountHolder> Holders { get; } = [];
    public List<LegacyTransaction> Transactions { get; } = [];
    public int DuplicatesSeeded { get; private set; }

    public LegacyBankGenerator(DateOnly extractDate, int historyMonths)
    {
        this.extractDate = extractDate;
        historyStart = new DateOnly(extractDate.Year, extractDate.Month, 1).AddMonths(-historyMonths);

        foreach (var s in States)
            for (int i = 0; i < Math.Max(1, (int)(s.Weight * 12)); i++)
                branches.Add(new Branch($"BR{branches.Count + 1:000}", s.State, $"99{s.BsbDigit}-{branches.Count + 1:000}"));

        // Shared pools, so thousands of customers pay the same employers, shops and billers
        string[] Pool(int n, string suffix) =>
            [.. Enumerable.Range(0, n).Select(_ => $"{f.Name.LastName()} {suffix}".ToUpperInvariant())];
        employers = [.. Enumerable.Range(0, 150).Select(_ => $"{f.Name.LastName()} {f.Company.CompanySuffix()}".ToUpperInvariant())];
        superFunds = Pool(5, "SUPER");
        grocers = Pool(4, "SUPERMARKETS");
        fuel = Pool(4, "FUEL");
        pharmacies = Pool(3, "PHARMACY");
        online = Pool(10, "ONLINE");
        telcos = Pool(4, "MOBILE");
        broadband = Pool(4, "BROADBAND");
        insurers = Pool(5, "INSURANCE");
        healthFunds = Pool(4, "HEALTH FUND");
        energy = Pool(4, "ENERGY");
        water = Pool(3, "WATER");
        streaming = Pool(4, "STREAMING");
        gyms = Pool(5, "FITNESS");
        agents = Pool(8, "REAL ESTATE");
    }

    // One household at a time: `flush` writes out the household's rows, then they're dropped,
    // so memory stays flat however big the bank gets
    public void Generate(int customerCount, Action flush)
    {
        for (var made = 0; made < customerCount;)
        {
            GenerateHousehold();
            made += Customers.Count;
            flush();
            Customers.Clear();
            Accounts.Clear();
            Holders.Clear();
            Transactions.Clear();
        }
    }

    void GenerateHousehold()
    {
        var st = Weighted(States.Select(s => (s, s.Weight)).ToArray());
        var home = new Address(f.Address.StreetAddress(), f.Address.City(), st.State, Postcode(st.State));
        var branch = f.PickRandom(branches.Where(b => b.State == st.State));
        var h = new Household { Suburb = home.Suburb.ToUpperInvariant(), Quality = Normal(0, 1) };

        var age = (int)Math.Clamp(Normal(47, 17), 18, 95);
        var couple = age >= 21 && f.Random.Bool(0.45f);
        var marital = couple ? Weighted(("MARRIED", 0.7), ("DE_FACTO", 0.3))
            : age < 30 ? "SINGLE"
            : Weighted(("SINGLE", 0.5), ("DIVORCED", 0.3), ("WIDOWED", age > 65 ? 0.4 : 0.03));
        var dependants = age is >= 25 and <= 55 && f.Random.Bool(couple ? 0.65f : 0.2f) ? f.Random.Int(1, 3) : 0;
        var housing = age < 25 ? Weighted(("LIVING_WITH_PARENTS", 0.5), ("RENTING", 0.45), ("MORTGAGE", 0.05))
            : age >= 65 ? Weighted(("OWNER", 0.7), ("MORTGAGE", 0.1), ("RENTING", 0.2))
            : Weighted(("MORTGAGE", 0.45), ("RENTING", 0.33), ("OWNER", 0.15), ("LIVING_WITH_PARENTS", 0.07));

        var p1 = NewPerson(home, branch, age, marital, dependants, housing, null, null);
        h.People.Add(p1);
        if (couple)
            h.People.Add(NewPerson(home, branch, (int)Math.Clamp(age + Normal(0, 4), 18, 95), marital, dependants, housing,
                f.Random.Bool(0.65f) ? p1.Last : null, p1.Since));

        // Former customers: every account closed, but the customer record is still on file
        var former = f.Random.Bool(0.04f);
        // Deceased estate: accounts frozen until the executor acts
        h.Frozen = !couple && !former && age > 70 && f.Random.Bool(0.05f);
        p1.Row.Deceased = h.Frozen;

        OpenAccounts(h, branch, st.MedianHome, housing, former);
        if (!former)
        {
            PlanSpending(h, housing);
            Simulate(h);
            Finalise(h);
        }
        CreditProfile(h);
        MaybeDuplicate(h);
    }

    Person NewPerson(Address home, Branch branch, int age, string marital, int dependants, string housing,
        string? lastName, DateOnly? joinedWith)
    {
        var gender = Weighted(("F", 0.505), ("M", 0.49), ("X", 0.005));
        var nameGender = gender switch { "M" => Name.Gender.Male, "F" => Name.Gender.Female, _ => f.PickRandom<Name.Gender>() };
        var first = f.Name.FirstName(nameGender);
        var last = lastName ?? f.Name.LastName();
        var dob = extractDate.AddYears(-age).AddDays(-f.Random.Int(1, 364));
        var adult = dob.AddYears(18);
        var since = joinedWith is { } j && f.Random.Bool(0.6f) ? Max(j, adult)
            : RecentDate(Max(adult, BankFounded), 9, extractDate.AddDays(-14));
        var oldCore = since < CoreConversion;

        var employment = age >= 67 ? Weighted(("RETIRED", 0.85), ("PART_TIME", 0.08), ("SELF_EMPLOYED", 0.07))
            : age < 23 ? Weighted(("STUDENT", 0.35), ("CASUAL", 0.3), ("PART_TIME", 0.15), ("FULL_TIME", 0.2))
            : Weighted(("FULL_TIME", 0.6), ("PART_TIME", 0.14), ("CASUAL", 0.06), ("SELF_EMPLOYED", 0.1),
                ("UNEMPLOYED", 0.04), ("HOME_DUTIES", dependants > 0 ? 0.06 : 0.01), ("RETIRED", age >= 55 ? 0.15 : 0));
        string? occupation = null, employer = null;
        decimal income;
        if (employment == "SELF_EMPLOYED")
        {
            var trade = f.PickRandom(Trades);
            (occupation, employer) = (trade.Name, $"{last} {trade.Business}".ToUpperInvariant());
            income = LogNormal(trade.Median, 0.5);
        }
        else if (employment is "FULL_TIME" or "PART_TIME" or "CASUAL")
        {
            var job = f.PickRandom(Occupations);
            occupation = job.Name;
            employer = job.Name switch
            {
                "Registered Nurse" or "Paramedic" => "STATE HEALTH SERVICE",
                "Teacher" => "DEPT OF EDUCATION",
                "Police Officer" => "STATE POLICE",
                "Public Servant" => f.PickRandom("STATE GOVERNMENT", "REGIONAL COUNCIL", "COMMONWEALTH DEPT"),
                _ => f.PickRandom(employers),
            };
            income = LogNormal(job.Median * (employment switch { "FULL_TIME" => 1.0, "PART_TIME" => 0.55, _ => 0.35 }), 0.2);
        }
        else income = employment switch
        {
            "RETIRED" => LogNormal(32_000, 0.35),       // pension and/or super drawdown
            "UNEMPLOYED" => LogNormal(21_000, 0.15),    // government benefit
            "STUDENT" => LogNormal(16_000, 0.4),
            _ => dependants > 0 ? LogNormal(12_000, 0.4) : 0, // family payments
        };
        income = Math.Round(income / 1000) * 1000;      // applications record income to the nearest thousand

        var title = f.Random.Bool(0.02f) ? "Dr" : gender switch
        {
            "M" => "Mr",
            "X" => "Mx",
            _ when marital == "MARRIED" => Weighted(("Mrs", 0.6), ("Ms", 0.4)),
            _ when age < 30 => Weighted(("Miss", 0.4), ("Ms", 0.6)),
            _ => "Ms",
        };

        var residency = f.Random.Bool(0.96f) ? "AU" : f.PickRandom("NZ", "GB", "IN", "CN", "US", "PH");
        var tfn = residency != "AU" ? "NON_RESIDENT"
            : employment == "RETIRED" && f.Random.Bool(0.3f) ? "EXEMPT"     // pensioners can claim an exemption
            : f.Random.Bool(0.08f) ? "NOT_PROVIDED" : "PROVIDED";
        var kyc = since < AmlCommencement && f.Random.Bool(0.55f) ? "PRE_COMMENCEMENT"
            : f.Random.Bool(0.015f) ? "PENDING" : "VERIFIED";
        var pep = f.Random.Bool(0.004f);
        var risk = pep || (residency != "AU" && f.Random.Bool(0.3f)) ? "HIGH"
            : Weighted(("LOW", 0.9), ("MEDIUM", 0.09), ("HIGH", 0.01));

        var mobile = f.Random.Bool(age > 75 ? 0.25f : 0.04f) ? null : Mobile();
        var email = f.Random.Bool(since.Year < 2005 ? 0.5f : 0.08f) ? null
            : f.Internet.Email(first, last, f.PickRandom("example.com", "example.net", "example.org")).ToLowerInvariant();
        var contact = email == null ? (mobile == null ? "POST" : Weighted(("SMS", 0.6), ("POST", 0.4)))
            : Weighted(("EMAIL", 0.6), ("SMS", 0.25), ("POST", age > 70 ? 0.4 : 0.15));

        // ~5% of names were keyed in carelessly: all lowercase, or padded with spaces
        var (rowFirst, rowLast) = (first, last);
        if (f.Random.Bool(0.05f))
            (rowFirst, rowLast) = f.Random.Bool() ? (first.ToLowerInvariant(), last.ToLowerInvariant()) : ($"  {first}", $"{last}   ");

        var row = new LegacyCustomer
        {
            CustId = NextCustId(),
            Title = oldCore ? title.ToUpperInvariant() : title,
            FirstName = rowFirst,
            MiddleName = f.Random.Bool(0.6f) ? f.Name.FirstName(nameGender) : null,
            LastName = rowLast,
            Gender = gender,
            DateOfBirth = Date(dob, oldCore),
            AddressLine = home.Line,
            Suburb = home.Suburb,
            State = home.State,
            Postcode = home.Postcode,
            Mobile = mobile,
            Email = email,
            PreferredContact = contact,
            CustomerSince = Date(since, oldCore),
            HomeBranch = branch.Code,
            EmploymentStatus = employment,
            Occupation = occupation,
            Employer = employer,
            AnnualIncome = income,
            ResidentialStatus = housing,
            MaritalStatus = marital,
            Dependants = dependants,
            TfnStatus = tfn,
            TaxResidency = residency,
            KycStatus = kyc,
            KycIdType = kyc == "VERIFIED"
                ? Weighted(("DRIVERS_LICENCE", 0.65), ("PASSPORT", 0.25), ("PROOF_OF_AGE_CARD", 0.05), ("BIRTH_CERTIFICATE", 0.05))
                : null,
            // Pre-2007 customers who are verified had it done later, in a remediation program
            KycVerifiedDate = kyc != "VERIFIED" ? null
                : Date(since < AmlCommencement ? RandomDate(AmlCommencement, extractDate) : since, oldCore),
            AmlRiskRating = risk,
            Pep = pep,
            Vulnerable = f.Random.Bool(age > 80 ? 0.1f : 0.015f),
            MarketingConsent = f.Random.Bool(0.55f),
        };
        Customers.Add(row);

        var p = new Person { Row = row, First = first, Last = last, Dob = dob, Since = since, OldCore = oldCore, Age = age, Income = income };
        var net = AfterTax(income);
        switch (employment)
        {
            case "SELF_EMPLOYED":
                (p.PaidMonthly, p.Pay, p.PayDescription) = (true, Math.Round(net / 12, 2), $"TRANSFER FROM {employer}");
                break;
            case "RETIRED" when f.Random.Bool():
                (p.PaidMonthly, p.Pay, p.PayDescription) = (true, Math.Round(net / 12, 2), $"{f.PickRandom(superFunds)} PENSION PAYMENT");
                break;
            case "RETIRED" or "UNEMPLOYED" or "STUDENT" or "HOME_DUTIES":
                (p.Pay, p.PayDescription, p.PayAnchor) =
                    (Math.Round(net / 26, 2), employment == "RETIRED" ? "GOVT AGE PENSION" : "GOVT BENEFIT PAYMENT", Payday(f.Random.Int(1, 5)));
                break;
            default:
                (p.Pay, p.PayDescription, p.PayAnchor, p.CasualHours) =
                    (Math.Round(net / 26, 2), $"SALARY {employer}", Payday(f.PickRandom(3, 4)), employment == "CASUAL");
                break;
        }
        return p;
    }

    void OpenAccounts(Household h, Branch branch, int medianHome, string housing, bool former)
    {
        var all = h.People;
        var from = all.Max(p => p.Since);   // joint accounts can't predate either holder
        var income = all.Sum(p => p.Income);
        var age = all[0].Age;

        for (int i = f.Random.Int(former ? 1 : 0, 2); i > 0; i--)
            ClosedAccount(h, branch);
        if (former) return;

        var investor = housing != "MORTGAGE" || f.Random.Bool(0.15f);
        if (!h.Frozen && ((housing == "MORTGAGE" && f.Random.Bool(0.55f)) || (housing is "OWNER" or "RENTING" && f.Random.Bool(0.06f))))
            OpenHomeLoan(h, branch, from, medianHome, investor);

        if (h.Offset != null)
        {
            h.Main = h.Offset;
            // The old everyday account was closed when the offset took over as the main account
            if (from < h.Offset.Opened.AddMonths(-1))
                Close(Open(h, Kind.Everyday, all, branch, RandomDate(from, h.Offset.Opened.AddMonths(-1))), h.Offset.Opened);
        }
        else
        {
            h.Main = Open(h, Kind.Everyday, all, branch, from);
            h.Main.Opening = Math.Round(LogNormal(1_500 + (double)income / 40, 0.9), 2);
            if (f.Random.Bool(0.06f)) h.Main.Row.OverdraftLimit = f.Random.Int(5, 30) * 100;
        }
        h.Main.Row.MaskedCardNo = MaskedCard();

        if (f.Random.Bool(0.65f))
        {
            List<Person> owners = all.Count > 1 && f.Random.Bool(0.5f) ? all : [f.PickRandom(all)];
            var opened = RecentDate(owners.Max(p => p.Since), 8, extractDate.AddDays(-14));
            var s = Open(h, Kind.Savings, owners, branch, opened);
            s.Opening = Math.Round(LogNormal(age > 55 ? 25_000 : 9_000, 1.2), 2);
            if (opened < extractDate.AddYears(-3) && f.Random.Bool(0.06f))
            {
                // Untouched for years. Interest still lands, but that isn't customer activity.
                s.Row.Status = "DORMANT";
                s.Opening = Math.Round(LogNormal(300, 1), 2);
                s.LastActivity = RandomDate(Max(opened, extractDate.AddYears(-6)), extractDate.AddYears(-2));
            }
            else h.Savings = s;
        }

        if (f.Random.Bool(age >= 55 ? 0.3f : 0.08f))
        {
            List<Person> owners = all.Count > 1 && f.Random.Bool(0.5f) ? all : [f.PickRandom(all)];
            // Terms longer than the history window, so no rollover happens inside it
            var term = f.PickRandom(new[] { 6, 12, 12, 24, 36 }.Where(t => extractDate.AddMonths(-t) < historyStart));
            var opened = RecentDate(owners.Max(p => p.Since), 6, extractDate.AddDays(-7));
            var rate = term switch { 6 => 3.85m, 12 => 4.05m, 24 => 3.95m, _ => 3.90m };
            var td = Open(h, Kind.TermDeposit, owners, branch, opened, ("TDP", "Term Deposit", rate));
            // It has rolled over at each maturity, so the current term began within the last `term` months
            td.TermStart = opened > historyStart ? opened
                : RandomDate(Max(opened, extractDate.AddMonths(-term).AddDays(1)), historyStart);
            td.Opening = Math.Max(5_000, Math.Round(LogNormal(45_000, 0.8) / 1000) * 1000);
            td.Row.OriginalAmount = td.Opening;
            td.Row.TermMonths = term;
            td.Row.MaturityDate = Date(td.TermStart.Value.AddMonths(term), td.OldCore);
            td.Row.MaturityInstruction = Weighted(("ROLLOVER", 0.6), ("PAY_TO_ACCOUNT", 0.4));
            if (td.TermStart > opened) td.LastInterest = td.TermStart;
        }

        if (!h.Frozen && age < 70 && income >= 30_000 && f.Random.Bool(0.1f))
            OpenPersonalLoan(h, branch, f.PickRandom(all));

        if (!h.Frozen && income >= 30_000 && f.Random.Bool(0.35f))
            h.Card = OpenCard(h, branch, all.MaxBy(p => p.Income)!);
    }

    void OpenHomeLoan(Household h, Branch branch, DateOnly from, int medianHome, bool investment)
    {
        var opened = RecentDate(Max(from, extractDate.AddYears(-25)), 6, extractDate.AddDays(-20));
        var years = (extractDate.DayNumber - opened.DayNumber) / 365.25;
        var interestOnly = years < 5 && f.Random.Bool(investment ? 0.4f : 0.08f);
        var fixedRate = f.Random.Bool(0.2f);
        var term = Weighted((360, 0.85), (300, 0.1), (240, 0.05));
        // Property prices have grown roughly 5% a year, so older purchases were cheaper
        var value = Math.Round(LogNormal(medianHome / Math.Pow(1.05, years), 0.3) / 1000) * 1000;
        // Serviceability: lenders cap the loan at a multiple of income, whatever the property is worth
        var maxByIncome = h.People.Sum(p => p.Income) * (decimal)f.Random.Double(4, 6.5);
        var amount = Math.Round(Math.Min(value * (decimal)Math.Clamp(Normal(78, 9), 40, 95) / 100, maxByIncome) / 1000) * 1000;
        if (amount < 50_000) return;
        var lvr = Math.Round(amount / value * 100, 1);
        // Long-standing borrowers drift above the rate offered to new ones
        var rate = 5.84m + (investment ? 0.30m : 0) + (interestOnly ? 0.35m : 0) - (fixedRate ? 0.15m : 0)
                   + Math.Round((decimal)Math.Min(years, 10) * 0.04m, 2);
        var monthly = interestOnly ? Math.Round(amount * rate / 1200, 2) : Repayment(amount, rate, term);
        var fortnightly = f.Random.Bool(0.3f);

        var loan = Open(h, Kind.HomeLoan, h.People, branch, opened,
            fixedRate ? ("HLF", "Home Loan Fixed", rate) : ("HLV", "Home Loan Variable", rate));
        var r = loan.Row;
        r.OriginalAmount = amount;
        r.TermMonths = term;
        r.MaturityDate = Date(opened.AddMonths(term), loan.OldCore);
        r.RepaymentAmount = fortnightly ? Math.Round(monthly / 2, 2) : monthly;
        r.RepaymentFrequency = fortnightly ? "FORTNIGHTLY" : "MONTHLY";
        r.RepaymentType = interestOnly ? "IO" : "PI";
        r.LoanPurpose = investment ? "INVESTMENT" : "OWNER_OCCUPIED";
        r.SecurityValue = value;
        r.OriginalLvr = lvr;
        if (fixedRate) r.FixedRateExpiry = Date(RandomDate(extractDate.AddMonths(1), extractDate.AddYears(3)), loan.OldCore);
        loan.Extra = f.Random.Bool(0.3f) ? Math.Round(LogNormal(400, 0.6)) : 0;
        loan.DueDay = Math.Min(opened.Day, 28);
        if (fortnightly)
            loan.FortnightAnchor = opened.AddDays(14 * Math.Max(1, (historyStart.DayNumber - opened.DayNumber + 13) / 14));

        if (opened > historyStart)
            loan.Opening = -amount;   // drawn down inside the window
        else
        {
            // Half-monthly payments made fortnightly add up to 13 monthly payments a year
            var perMonth = (fortnightly ? Math.Round(monthly * 13 / 12, 2) : monthly) + loan.Extra;
            var (owed, paidOffAfter) = interestOnly ? (amount, null) : RollForward(amount, rate, perMonth, MonthsBetween(opened, historyStart));
            if (paidOffAfter is int months)
            {
                Close(loan, opened.AddMonths(months));
                return;
            }
            loan.Opening = -owed;
        }

        if (!investment && f.Random.Bool(0.35f))
        {
            var o = Open(h, Kind.Offset, h.People, branch, opened);
            o.Opening = Math.Round(LogNormal(22_000, 1.0), 2);
            o.Row.LinkedAcctNo = r.AcctNo;
            r.LinkedAcctNo = o.Row.AcctNo;
            h.Offset = o;
        }
        MaybeArrears(loan, h.Quality);
    }

    void OpenPersonalLoan(Household h, Branch branch, Person who)
    {
        var term = f.PickRandom(12, 24, 36, 48, 60, 60, 84);
        var opened = RandomDate(Max(who.Since, extractDate.AddMonths(-term + 1)), extractDate.AddDays(-20));
        var amount = Math.Clamp(Math.Round(LogNormal(18_000, 0.5) / 500) * 500, 3_000, 60_000);
        // Priced on credit risk
        var rate = Math.Round((decimal)Math.Clamp(11.5 - 2.5 * h.Quality, 7.49, 18.99), 2);
        var monthly = Repayment(amount, rate, term);

        var loan = Open(h, Kind.PersonalLoan, [who], branch, opened, ("PLN", "Personal Loan", rate));
        var r = loan.Row;
        r.OriginalAmount = amount;
        r.TermMonths = term;
        r.MaturityDate = Date(opened.AddMonths(term), loan.OldCore);
        r.RepaymentAmount = monthly;
        r.RepaymentFrequency = "MONTHLY";
        r.RepaymentType = "PI";
        r.LoanPurpose = Weighted(("CAR", 0.4), ("DEBT_CONSOLIDATION", 0.2), ("HOME_IMPROVEMENT", 0.15),
            ("TRAVEL", 0.1), ("WEDDING", 0.05), ("OTHER", 0.1));
        loan.DueDay = Math.Min(opened.Day, 28);
        loan.Opening = opened > historyStart ? -amount
            : -RollForward(amount, rate, monthly, MonthsBetween(opened, historyStart)).Owed;
        MaybeArrears(loan, h.Quality);
    }

    Acct OpenCard(Household h, Branch branch, Person who)
    {
        var opened = RecentDate(who.Since, 6, extractDate.AddDays(-20));
        var rewards = f.Random.Bool(0.55f);
        var card = Open(h, Kind.CreditCard, [who], branch, opened,
            rewards ? ("CCR", "Rewards Credit Card", 20.99m) : ("CCS", "Low Rate Credit Card", 12.99m));
        var limit = Math.Clamp(Math.Round(who.Income * (decimal)f.Random.Double(0.04, 0.2) / 500) * 500, 1_000, 30_000);
        card.Row.CreditLimit = limit;
        card.Row.MaskedCardNo = MaskedCard();
        card.PaysInFull = f.Random.Bool(0.55f);   // "transactors" clear the card every month, "revolvers" carry a balance
        card.DueDay = f.Random.Int(1, 28);
        if (opened <= historyStart)
        {
            card.Opening = -Math.Round(limit * (decimal)(card.PaysInFull ? f.Random.Double(0.02, 0.25) : f.Random.Double(0.3, 0.9)), 2);
            card.StatementOwed = -card.Opening;
            card.MinDue = MinPayment(card.StatementOwed);
            card.CardDue = historyStart.AddDays(f.Random.Int(1, 24));
        }
        MaybeArrears(card, h.Quality);
        return card;
    }

    // An account the customer opened and closed before the history window
    void ClosedAccount(Household h, Branch branch)
    {
        var who = f.PickRandom(h.People);
        var latest = historyStart.AddDays(-1);
        if (who.Since > latest.AddMonths(-6)) return;
        var opened = RandomDate(who.Since, latest.AddMonths(-6));
        var closed = RandomDate(opened.AddMonths(3), latest);
        var kind = Weighted((Kind.Savings, 0.35), (Kind.Everyday, 0.2), (Kind.CreditCard, 0.2), (Kind.PersonalLoan, 0.15), (Kind.TermDeposit, 0.1));
        var a = Open(h, kind, [who], branch, opened, kind switch
        {
            Kind.CreditCard => ("CCS", "Low Rate Credit Card", 12.99m),
            Kind.PersonalLoan => ("PLN", "Personal Loan", 10.99m),
            Kind.TermDeposit => ("TDP", "Term Deposit", 4.05m),
            _ => DepositProduct(kind, opened, who.Age),
        });
        switch (kind)
        {
            case Kind.CreditCard:
                a.Row.CreditLimit = f.Random.Int(2, 20) * 500;
                a.Row.MaskedCardNo = MaskedCard();
                break;
            case Kind.PersonalLoan:
                a.Row.OriginalAmount = f.Random.Int(6, 60) * 500;
                a.Row.TermMonths = Math.Max(12, (MonthsBetween(opened, closed) + 11) / 12 * 12);
                a.Row.LoanPurpose = "CAR";
                break;
            case Kind.TermDeposit:
                a.Row.OriginalAmount = f.Random.Int(5, 100) * 1000;
                a.Row.TermMonths = 12;
                break;
        }
        Close(a, closed);
    }

    // Product names change over the years; accounts opened before the 2010 conversion sit on retired products
    static (string Code, string Name, decimal Rate) DepositProduct(Kind kind, DateOnly opened, int age) => kind switch
    {
        Kind.Everyday when opened < CoreConversion => ("EVR00", "Classic Cheque Account", 0m),
        Kind.Everyday when age >= 60 => ("EVR02", "Seniors Access", 0m),
        Kind.Everyday => ("EVR01", "Everyday Access", 0m),
        Kind.Offset => ("OFF01", "Mortgage Offset", 0m),
        Kind.Savings when opened < CoreConversion => ("SAV00", "Passbook Savings", 0.25m),
        Kind.Savings when opened.Year >= 2020 => ("SAV02", "Bonus Saver", 4.55m),
        Kind.Savings => ("SAV01", "Online Saver", 3.90m),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    Acct Open(Household h, Kind kind, List<Person> owners, Branch branch, DateOnly opened,
        (string Code, string Name, decimal Rate)? product = null)
    {
        var (code, name, rate) = product ?? DepositProduct(kind, opened, owners[0].Age);
        if (opened.Year >= 2018 && f.Random.Bool(0.4f)) branch = digital;   // opened online
        var oldCore = opened < CoreConversion;
        nextAcctNo += f.Random.Int(1, 9);
        // The old core's account title field holds 30 characters, so long joint names get cut off
        var accountName = string.Join(" & ", owners.Select(p => $"{p.First} {p.Last}")).ToUpperInvariant();
        var row = new LegacyAccount
        {
            AcctNo = nextAcctNo.ToString(),
            Bsb = f.Random.Bool(0.1f) ? branch.Bsb.Replace("-", "") : branch.Bsb,
            ProductCode = code,
            ProductName = name,
            AccountName = accountName[..Math.Min(30, accountName.Length)],
            Branch = branch.Code,
            OpenDate = Date(opened, oldCore),
            Status = "OPEN",
            InterestRate = rate,
        };
        Accounts.Add(row);
        for (int i = 0; i < owners.Count; i++)
            Holders.Add(new LegacyAccountHolder { AcctNo = row.AcctNo, CustId = owners[i].Row.CustId, Role = i == 0 ? "PRIMARY" : "JOINT" });

        var a = new Acct { Row = row, Owners = owners, Kind = kind, Opened = opened, OldCore = oldCore, Tfn = owners[0].Row.TfnStatus };
        h.Accounts.Add(a);
        return a;
    }

    void Close(Acct a, DateOnly closed)
    {
        a.Row.Status = "CLOSED";
        a.Row.CloseDate = a.Row.LastActivityDate = Date(closed, a.OldCore);
    }

    void MaybeArrears(Acct a, double quality)
    {
        a.SlipChance = quality < -1 ? 0.08 : 0.01;
        if (!f.Random.Bool(quality < -1 ? 0.15f : 0.02f)) return;
        // Stops paying at some point inside the history window
        a.MissFrom = extractDate.AddDays(-f.Random.Int(5, extractDate.DayNumber - historyStart.DayNumber - 5));
    }

    void PlanSpending(Household h, string housing)
    {
        var owns = housing is "OWNER" or "MORTGAGE";
        var netFortnight = h.People.Sum(p => p.PaidMonthly ? p.Pay * 12 / 26 : p.Pay);
        string Ref() => f.Random.Number(10_000_000, 99_999_999).ToString();
        void Monthly(string payee, decimal amount, bool cardOk = false) =>
            h.Bills.Add(new Bill("DD", $"DIRECT DEBIT {payee} {Ref()}", amount, f.Random.Int(1, 28), 1, 0,
                cardOk && h.Card != null && f.Random.Bool()));
        void Quarterly(string payee, decimal amount) =>
            h.Bills.Add(new Bill("BPY", $"BPAY {payee} CRN {Ref()}", amount, f.Random.Int(1, 28), 3, f.Random.Int(0, 2), false));

        foreach (var _ in h.People) Monthly(f.PickRandom(telcos), f.Random.Int(35, 90));
        if (f.Random.Bool(0.85f)) Monthly(f.PickRandom(broadband), f.Random.Int(65, 110));
        if (f.Random.Bool(0.8f)) Monthly(f.PickRandom(insurers) + " MOTOR", f.Random.Int(60, 180));
        if (owns) Monthly(f.PickRandom(insurers) + " HOME", f.Random.Int(90, 250));
        if (f.Random.Bool(0.55f)) Monthly(f.PickRandom(healthFunds), f.Random.Int(180, 480));
        for (int i = f.Random.Int(0, 3); i > 0; i--) Monthly(f.PickRandom(streaming), f.Random.Int(10, 25), cardOk: true);
        if (f.Random.Bool(0.25f)) Monthly(f.PickRandom(gyms), f.Random.Int(45, 80), cardOk: true);
        if (housing == "MORTGAGE" && !h.Accounts.Any(a => a.Kind == Kind.HomeLoan && a.Row.Status != "CLOSED"))
            Monthly("HOME LOAN OTHER FI", Math.Round(Math.Min(LogNormal(2_800, 0.35), netFortnight * 26 / 12 * 0.35m), 2));
        Quarterly(f.PickRandom(energy), f.Random.Int(280, 750));
        if (owns)
        {
            Quarterly(f.PickRandom(water), f.Random.Int(180, 380));
            Quarterly($"{h.Suburb} COUNCIL RATES", f.Random.Int(420, 800));
        }
        if (housing == "RENTING")
        {
            // Weekly rent, paid fortnightly. Low earners rent cheaper places or share.
            h.Rent = Math.Round(Math.Clamp(LogNormal(560, 0.2), 150, Math.Max(150, netFortnight / 2 * 0.45m)) / 5) * 5 * 2;
            h.RentAnchor = historyStart.AddDays(f.Random.Int(0, 13));
            h.RentPayee = f.PickRandom(agents);
        }

        var loans = h.Accounts.Where(a => a.Kind is Kind.HomeLoan or Kind.PersonalLoan && a.Row.Status != "CLOSED");
        var fixedFortnight = h.Rent
            + h.Bills.Sum(b => b.Amount / b.EveryMonths) * 12 / 26
            + loans.Sum(a => (a.Row.RepaymentFrequency == "FORTNIGHTLY" ? a.Row.RepaymentAmount!.Value : a.Row.RepaymentAmount!.Value * 12 / 26)
                             + a.Extra * 12 / 26);
        h.SaveRate = h.Savings == null ? 0 : (decimal)Math.Clamp(Normal(0.10, 0.07), 0, 0.3);
        var spendRatio = (decimal)Math.Clamp(Normal(0.92, 0.1), 0.6, 1.15);
        // What's left after fixed costs and saving gets spent. When fixed costs eat everything, people
        // still buy groceries, and those are the households that end up overdrawn.
        h.DailySpend = Math.Max(netFortnight * 0.3m, (netFortnight - fixedFortnight) * (1 - h.SaveRate)) * spendRatio / 14;

        h.Grocers = [.. f.PickRandom(grocers, 2).Select(g => $"{g} {h.Suburb}")];
        h.Eateries = [.. Enumerable.Range(0, 4).Select(_ =>
            $"{f.Name.LastName()}'S {f.PickRandom("CAFE", "PIZZA", "THAI", "SUSHI", "BAKERY", "BURGERS", "HOTEL")} {h.Suburb}".ToUpperInvariant())];
    }

    void Simulate(Household h)
    {
        foreach (var a in h.Accounts.Where(a => a.Row.Status != "CLOSED" && a.Opened <= historyStart))
            Post(a, historyStart, "BFW", "BALANCE BROUGHT FORWARD", a.Opening, "SYSTEM");

        for (var d = historyStart; d <= extractDate; d = d.AddDays(1))
        {
            foreach (var a in h.Accounts.Where(a => a.Opened == d && d > historyStart && a.Row.Status != "CLOSED" && a.Opening != 0))
                Post(a, d, a.Opening < 0 ? "DRW" : "DEP", a.Opening < 0 ? "LOAN DRAWDOWN" : "INITIAL DEPOSIT FROM OTHER FI",
                    a.Opening, a.Opening < 0 ? "BRANCH" : "NPP", customer: true);

            if (!h.Frozen && IsOpen(h.Main!, d))
            {
                Income(h, d);
                Bills(h, d);
                Repayments(h, d);
                CardCycle(h, d);
                Spending(h, d);
                Sweep(h, d);
            }
            EndOfDay(h, d);
        }
    }

    void Income(Household h, DateOnly d)
    {
        foreach (var p in h.People)
        {
            if (p.Pay == 0 || d < p.Since) continue;
            var payday = p.PaidMonthly ? d.Day == 28 : d >= p.PayAnchor && (d.DayNumber - p.PayAnchor.DayNumber) % 14 == 0;
            if (!payday) continue;
            var amount = p.CasualHours ? Math.Round(p.Pay * (decimal)f.Random.Double(0.6, 1.3), 2) : p.Pay;
            Post(h.Main!, d, p.PaidMonthly ? "TFR" : "SAL", p.PayDescription, amount, "DIRECT_CREDIT", customer: true);
            if (h.Savings is { } s && IsOpen(s, d) && h.SaveRate > 0 && h.Main!.Row.Balance > 1_000)
                Transfer(h.Main, s, d, Math.Round(amount * h.SaveRate));
        }
    }

    // The bank honours a direct debit or scheduled BPAY that overdraws the account by up to $500
    // (then charges a fee at month end). Anything bigger is dishonoured and nothing posts.
    const decimal InformalOverdraft = 500;

    void Bills(Household h, DateOnly d)
    {
        if (h.Rent > 0 && d >= h.RentAnchor && (d.DayNumber - h.RentAnchor.DayNumber) % 14 == 0 && Available(h.Main!) + InformalOverdraft >= h.Rent)
            Post(h.Main!, d, "BPY", $"BPAY {h.RentPayee} RENT", -h.Rent, "BPAY", customer: true);
        foreach (var b in h.Bills.Where(b => b.Day == d.Day && (d.Month - 1) % b.EveryMonths == b.MonthOffset))
        {
            // Usage-based bills (energy, water) change every quarter
            var amount = b.Code == "BPY" ? Math.Round(b.Amount * (decimal)f.Random.Double(0.8, 1.2), 2) : b.Amount;
            var acct = b.OnCard && h.Card is { } c && IsOpen(c, d) && Available(c) >= amount ? c : h.Main!;
            if (Available(acct) + (acct == h.Main ? InformalOverdraft : 0) >= amount)
                Post(acct, d, b.Code, b.Description, -amount, b.Code == "DD" ? "DIRECT_DEBIT" : "BPAY", customer: true);
        }
    }

    void Repayments(Household h, DateOnly d)
    {
        foreach (var loan in h.Accounts.Where(a => a.Kind is Kind.HomeLoan or Kind.PersonalLoan && IsOpen(a, d) && d > a.Opened))
        {
            var due = loan.FortnightAnchor is { } anchor
                ? d >= anchor && (d.DayNumber - anchor.DayNumber) % 14 == 0
                : d.Day == loan.DueDay && d >= loan.Opened.AddDays(28);
            if (!due || loan.Row.Balance >= 0) continue;
            if (loan.MissFrom <= d)
            {
                loan.FirstMissed ??= d;
                loan.Arrears += loan.Row.RepaymentAmount!.Value;
                continue;
            }
            var amount = Math.Min(loan.Row.RepaymentAmount!.Value + loan.Extra, -loan.Row.Balance);
            Post(h.Main!, d, "LRP", $"LOAN REPAYMENT {loan.Row.AcctNo}", -amount, "DIRECT_DEBIT", customer: true);
            Post(loan, d, "LRP", "REPAYMENT THANK YOU", amount, "DIRECT_DEBIT", customer: true);
        }
    }

    void CardCycle(Household h, DateOnly d)
    {
        if (h.Card is not { } c || !IsOpen(c, d)) return;
        if (d.Day == c.DueDay)   // statement day
        {
            if (!c.PaysInFull && c.Row.Balance < 0)
                Post(c, d, "INT", "INTEREST CHARGED ON PURCHASES", -Math.Round(-c.Row.Balance * c.Row.InterestRate / 1200, 2), "SYSTEM");
            if (c.Row.ProductCode == "CCR" && d.Month == c.Opened.Month && d > c.Opened)
                Post(c, d, "FEE", "ANNUAL CARD FEE", -149m, "SYSTEM");
            c.StatementOwed = Math.Max(0, -c.Row.Balance);
            c.MinDue = MinPayment(c.StatementOwed);
            c.CardDue = d.AddDays(25);
            (c.LastCycleSpend, c.CycleSpend) = (c.CycleSpend, 0);
        }
        if (d == c.CardDue && c.StatementOwed > 0)
        {
            if (c.MissFrom <= d)
            {
                c.FirstMissed ??= d;
                c.Arrears += c.MinDue;
                return;
            }
            // Revolvers roughly cover the month's new spending and carry the old balance
            var amount = c.PaysInFull ? c.StatementOwed
                : Math.Min(c.StatementOwed, Math.Max(c.MinDue, Math.Round(c.LastCycleSpend * (decimal)f.Random.Double(0.85, 1.15), 2)));
            // Short of money, people pay what they can, but at least the minimum
            amount = Math.Min(amount, Math.Max(c.MinDue, Available(h.Main!)));
            Post(h.Main!, d, "TFR", $"PAYMENT TO CARD {c.Row.MaskedCardNo![^4..]}", -amount, "ONLINE", customer: true);
            Post(c, d, "PMT", "PAYMENT RECEIVED THANK YOU", amount, "ONLINE", customer: true);
        }
    }

    void Spending(Household h, DateOnly d)
    {
        // Purchases in the last two days are still authorisations when the extract runs
        var pending = d > extractDate.AddDays(-2);
        foreach (var (category, share, everyDays) in SpendCategories)
        {
            if (!f.Random.Bool((float)(1 / everyDays))) continue;
            var amount = Math.Round(h.DailySpend * share * (decimal)everyDays * (decimal)f.Random.Double(0.5, 1.5), 2);
            switch (category)
            {
                case "CASH":
                    amount = Math.Max(20, Math.Round(amount / 20) * 20);
                    if (Available(h.Main!) >= amount)
                        Post(h.Main!, d, "ATM", $"ATM WITHDRAWAL {h.Suburb}", -amount, "ATM", customer: true);
                    break;
                case "PEOPLE":
                    if (Available(h.Main!) >= amount)
                        Post(h.Main!, d, "NPP", $"NPP PAYMENT TO {f.Name.FirstName()} {f.Name.LastName()}".ToUpperInvariant(), -amount, "NPP", customer: true);
                    break;
                default:
                    var merchant = category switch
                    {
                        "GROCERIES" => f.PickRandom(h.Grocers),
                        "FUEL" => $"{f.PickRandom(fuel)} {h.Suburb}",
                        "DINING" => f.PickRandom(h.Eateries),
                        "HEALTH" => $"{f.PickRandom(pharmacies)} {h.Suburb}",
                        _ => f.PickRandom(online),
                    };
                    Purchase(h, d, pending, merchant, amount, online: category == "SHOPPING");
                    break;
            }
        }
        if (f.Random.Bool(1 / 45f))
            Purchase(h, d, pending, $"{f.Name.LastName()} {f.PickRandom("AUTO REPAIRS", "TRAVEL", "FURNITURE", "ELECTRICAL", "MEDICAL CENTRE")}".ToUpperInvariant(),
                Math.Round(LogNormal(900, 0.6), 2), online: false);
    }

    void Purchase(Household h, DateOnly d, bool pending, string merchant, decimal amount, bool online)
    {
        var onCard = h.Card is { } c && IsOpen(c, d) && f.Random.Bool(0.6f) && Available(c) >= amount;
        var acct = onCard ? h.Card! : h.Main!;
        if (Available(acct) < amount) return;   // declined at the terminal
        var desc = onCard ? $"{merchant} AU" : $"{(online ? "ONLINE" : "EFTPOS")} PURCHASE {merchant} AU";
        var channel = onCard ? "CARD" : online ? "ONLINE" : "EFTPOS";
        // Card purchases settle a day or two after the customer taps
        var tapped = pending ? d : d.AddDays(-f.Random.Int(0, 2));
        Post(acct, d, "POS", desc, -amount, channel, customer: true, txnDate: tapped < acct.Opened ? d : tapped, pending: pending);
    }

    // Customers top up an overdrawn main account from savings when they notice
    void Sweep(Household h, DateOnly d)
    {
        if (h.Main!.Row.Balance >= 0 || h.Savings is not { } s || !IsOpen(s, d) || s.Row.Balance <= 0 || !f.Random.Bool(0.7f)) return;
        Transfer(s, h.Main, d, Math.Min(s.Row.Balance, Math.Ceiling((-h.Main.Row.Balance + 300) / 100) * 100));
    }

    void EndOfDay(Household h, DateOnly d)
    {
        var monthEnd = d.AddDays(1).Day == 1;
        foreach (var a in h.Accounts.Where(a => IsOpen(a, d)))
        {
            // Interest accrues daily on the closing balance and is posted at month end
            var interestBase = a.Kind switch
            {
                Kind.Savings => Math.Max(0, a.Row.Balance),
                // The offset balance is deducted before home loan interest is worked out
                Kind.HomeLoan => Math.Max(0, -a.Row.Balance - (h.Offset is { } o && IsOpen(o, d) ? Math.Max(0, o.Row.Balance) : 0)),
                Kind.PersonalLoan => -a.Row.Balance,
                _ => 0,
            };
            a.Accrued += interestBase * a.Row.InterestRate / 100 / 365;
            if (!monthEnd) continue;

            var interest = Math.Round(a.Accrued, 2);
            if (interest > 0)
            {
                if (a.Kind == Kind.Savings)
                {
                    Post(a, d, "INT", "INTEREST CREDIT", interest, "SYSTEM");
                    // Interest is taxed at source when the bank has no TFN, or the customer lives overseas
                    var withholding = a.Tfn switch { "NOT_PROVIDED" => 0.47m, "NON_RESIDENT" => 0.10m, _ => 0m };
                    if (withholding > 0)
                        Post(a, d, "WHT", a.Tfn == "NON_RESIDENT" ? "NON-RESIDENT WITHHOLDING TAX" : "TFN WITHHOLDING TAX",
                            -Math.Round(interest * withholding, 2), "SYSTEM");
                }
                else Post(a, d, "INT", "INTEREST CHARGED", -interest, "SYSTEM");
                a.Accrued = 0;
                a.LastInterest = d;
            }

            if (a == h.Main && !h.Frozen)
            {
                if (a.Row.ProductCode == "EVR00" && a.DepositsThisMonth < 2_000)
                    Post(a, d, "FEE", "MONTHLY ACCOUNT KEEPING FEE", -5m, "SYSTEM");
                if (a.Row.Balance < -a.Row.OverdraftLimit)
                    Post(a, d, "FEE", "OVERDRAWN ACCOUNT FEE", -10m, "SYSTEM");
            }
            a.DepositsThisMonth = 0;
        }
    }

    // Writes the end-of-extract state of each open account back onto its row
    void Finalise(Household h)
    {
        foreach (var a in h.Accounts.Where(a => a.Row.Status != "CLOSED"))
        {
            var r = a.Row;
            if (h.Frozen) r.Status = "FROZEN";
            switch (a.Kind)
            {
                case Kind.TermDeposit:
                    // Simple interest since the term started, paid at maturity
                    r.AccruedInterest = Math.Round(r.Balance * r.InterestRate / 100 * (extractDate.DayNumber - a.TermStart!.Value.DayNumber) / 365, 2);
                    break;
                case Kind.HomeLoan or Kind.PersonalLoan:
                    r.AccruedInterest = -Math.Round(a.Accrued, 2);
                    r.NextDueDate = Date(NextDue(a), a.OldCore);
                    ArrearsAndHistory(a);
                    break;
                case Kind.CreditCard:
                    r.AvailableBalance = Available(a);
                    r.RepaymentAmount = a.MinDue;
                    r.NextDueDate = Date(a.CardDue is { } due && due > extractDate ? due : NextDue(a).AddDays(25), a.OldCore);
                    ArrearsAndHistory(a);
                    break;
                default:
                    r.AvailableBalance = Available(a);
                    r.AccruedInterest = Math.Round(a.Accrued, 2);
                    break;
            }
            r.LastInterestDate = a.LastInterest is { } li ? Date(li, a.OldCore) : null;
            r.LastActivityDate = a.LastActivity is { } la ? Date(la, a.OldCore) : null;
        }
    }

    void ArrearsAndHistory(Acct a)
    {
        var r = a.Row;
        r.ArrearsDays = a.FirstMissed is { } m ? extractDate.DayNumber - m.DayNumber : 0;
        r.ArrearsAmount = a.Arrears;
        r.Hardship = r.ArrearsDays >= 30 && f.Random.Bool(0.4f);

        var history = new char[24];
        var thisMonth = new DateOnly(extractDate.Year, extractDate.Month, 1);
        for (int i = 0; i < 24; i++)
        {
            var monthStart = thisMonth.AddMonths(i - 23);
            var asAt = i == 23 ? extractDate : monthStart.AddMonths(1).AddDays(-1);
            if (asAt < a.Opened) history[i] = '-';
            else if (a.FirstMissed is { } miss && miss <= asAt) history[i] = Bucket(asAt.DayNumber - miss.DayNumber);
            else history[i] = monthStart < historyStart && f.Random.Bool((float)a.SlipChance) ? '1' : '0';
        }
        r.RepaymentHistory24m = new string(history);
    }

    // The bureau check happens at each credit application, so the score on file can be years old
    void CreditProfile(Household h)
    {
        foreach (var p in h.People)
        {
            var credit = h.Accounts.Where(a => a.Kind is Kind.HomeLoan or Kind.PersonalLoan or Kind.CreditCard && a.Owners.Contains(p)).ToList();
            if (credit.Count == 0) continue;
            var row = p.Row;
            row.CreditDefaults = h.Quality < -1.5 ? Weighted((0, 0.6), (1, 0.3), (2, 0.1)) : f.Random.Bool(0.03f) ? 1 : 0;
            row.Bankrupt = h.Quality < -2.2 && f.Random.Bool(0.3f);
            row.CreditScore = (int)Math.Clamp(Normal(830 + 130 * h.Quality, 40) - 140 * row.CreditDefaults.Value - (row.Bankrupt ? 300 : 0), 0, 1200);
            row.CreditScoreDate = Date(credit.Max(a => a.Opened), p.OldCore);
            row.CreditEnquiries5y = credit.Count(a => a.Opened > extractDate.AddYears(-5)) + f.Random.Int(0, 3);
        }
    }

    // Some customers were keyed in twice, usually when they opened something at another branch
    // years later and nobody found the existing record. Stage 3's de-duplication has to catch these.
    void MaybeDuplicate(Household h)
    {
        if (!f.Random.Bool(0.05f)) return;
        var p = f.PickRandom(h.People);
        var sole = h.Accounts.Where(a => a.Owners.Count == 1 && a.Owners[0] == p).ToList();
        if (sole.Count < 2) return;
        var moved = sole.MaxBy(a => a.Opened)!;
        var oldCore = moved.Opened < CoreConversion;
        // Day and month swapped at data entry
        var dob = p.Dob.Day <= 12 && f.Random.Bool(0.15f) ? new DateOnly(p.Dob.Year, p.Dob.Day, p.Dob.Month) : p.Dob;

        var dup = p.Row with
        {
            CustId = NextCustId(),
            FirstName = Weighted((p.First[..1], 0.4), (p.First.ToUpperInvariant(), 0.3), (p.First, 0.3)),
            MiddleName = null,
            LastName = f.Random.Bool(0.3f) ? Typo(p.Last) : p.Last,
            DateOfBirth = Date(dob, oldCore),
            AddressLine = Abbreviate(p.Row.AddressLine),
            Mobile = f.Random.Bool(0.5f) ? null : p.Row.Mobile,
            Email = f.Random.Bool(0.6f) ? null : p.Row.Email,
            CustomerSince = Date(moved.Opened, oldCore),
        };
        Customers.Add(dup);
        Holders.First(x => x.AcctNo == moved.Row.AcctNo).CustId = dup.CustId;
        DuplicatesSeeded++;
    }

    DateOnly NextDue(Acct a)
    {
        if (a.FortnightAnchor is { } anchor)
            return anchor > extractDate ? anchor : anchor.AddDays(((extractDate.DayNumber - anchor.DayNumber) / 14 + 1) * 14);
        var next = new DateOnly(extractDate.Year, extractDate.Month, a.DueDay);
        return next > extractDate ? next : next.AddMonths(1);
    }

    // A pending card purchase is only authorised: it holds funds (the available balance) but doesn't
    // move the balance until it posts
    void Post(Acct a, DateOnly d, string code, string desc, decimal amount, string channel,
        bool customer = false, DateOnly? txnDate = null, bool pending = false)
    {
        if (pending) a.Pending -= amount;
        else
        {
            a.Row.Balance += amount;
            if (a.Kind == Kind.CreditCard && amount < 0) a.CycleSpend -= amount;
            if (customer && amount > 0) a.DepositsThisMonth += amount;
        }
        if (customer) a.LastActivity = d;
        Transactions.Add(new LegacyTransaction
        {
            TxnId = $"T{++nextTxnId:0000000000}",
            AcctNo = a.Row.AcctNo,
            TxnDate = Date(txnDate ?? d),
            PostDate = pending ? null : Date(d),
            TxnCode = code,
            Description = desc,
            Amount = amount,
            RunningBalance = pending ? null : a.Row.Balance,
            Status = pending ? "PENDING" : "POSTED",
            Channel = channel,
        });
    }

    void Transfer(Acct from, Acct to, DateOnly d, decimal amount)
    {
        Post(from, d, "TFR", $"TRANSFER TO {to.Row.AcctNo}", -amount, "ONLINE", customer: true);
        Post(to, d, "TFR", $"TRANSFER FROM {from.Row.AcctNo}", amount, "ONLINE", customer: true);
    }

    static bool IsOpen(Acct a, DateOnly d) => a.Row.Status != "CLOSED" && a.Opened <= d;

    static decimal Available(Acct a) => a.Kind == Kind.CreditCard
        ? a.Row.CreditLimit!.Value + a.Row.Balance - a.Pending
        : a.Row.Balance + a.Row.OverdraftLimit - a.Pending;

    static decimal MinPayment(decimal owed) => Math.Min(owed, Math.Max(25, Math.Round(owed * 0.02m, 2)));

    // Standard annuity formula. Math.Pow only exists for double, so the result is rounded back to cents.
    static decimal Repayment(decimal principal, decimal rate, int months)
    {
        var r = (double)rate / 1200;
        return Math.Round((decimal)((double)principal * r / (1 - Math.Pow(1 + r, -months))), 2);
    }

    // Month by month the way the core does it: interest to the cent, then the repayment
    static (decimal Owed, int? PaidOffAfter) RollForward(decimal owed, decimal rate, decimal perMonth, int months)
    {
        for (int m = 1; m <= months; m++)
        {
            owed += Math.Round(owed * rate / 1200, 2) - perMonth;
            if (owed <= 0) return (0, m);
        }
        return (owed, null);
    }

    // Rough Australian resident tax (2026 rates) plus the Medicare levy
    static decimal AfterTax(decimal gross)
    {
        (decimal From, decimal Rate)[] brackets = [(18_200, 0.15m), (45_000, 0.30m), (135_000, 0.37m), (190_000, 0.45m)];
        decimal tax = 0;
        for (int i = 0; i < brackets.Length; i++)
        {
            var upper = i + 1 < brackets.Length ? brackets[i + 1].From : decimal.MaxValue;
            if (gross > brackets[i].From) tax += (Math.Min(gross, upper) - brackets[i].From) * brackets[i].Rate;
        }
        return gross - tax - (gross > 27_000 ? gross * 0.02m : 0);
    }

    // Days overdue to a reporting band
    static char Bucket(int days) => days switch { < 30 => '1', < 60 => '2', < 90 => '3', < 120 => '4', < 150 => '5', < 180 => '6', _ => 'X' };

    // The old core stored dates as text: yyyyMMdd before the 2010 conversion, dd/MM/yyyy after
    static string Date(DateOnly d, bool oldCore = false) => d.ToString(oldCore ? "yyyyMMdd" : "dd/MM/yyyy", CultureInfo.InvariantCulture);

    string Postcode(string state) => (state switch
    {
        "NSW" => f.Random.Number(2000, 2599),
        "ACT" => f.Random.Number(2600, 2618),
        "VIC" => f.Random.Number(3000, 3999),
        "QLD" => f.Random.Number(4000, 4999),
        "SA" => f.Random.Number(5000, 5799),
        "WA" => f.Random.Number(6000, 6797),
        "TAS" => f.Random.Number(7000, 7799),
        _ => f.Random.Number(800, 899),
    }).ToString("0000");

    // One number format per person, depending on who keyed it in
    string Mobile()
    {
        var digits = $"4{f.Random.Number(10_000_000, 99_999_999)}";
        return Weighted(($"0{digits[..3]} {digits[3..6]} {digits[6..]}", 0.7), ($"0{digits}", 0.2), ($"+61{digits}", 0.1));
    }

    string MaskedCard() => $"XXXX XXXX XXXX {f.Random.Number(1000, 9999)}";

    string NextCustId() => $"C{nextCustId += f.Random.Int(1, 3)}";

    string Typo(string s)
    {
        if (s.Length < 4) return s;
        var i = f.Random.Int(1, s.Length - 2);
        return s[..i] + s[i + 1] + s[i] + s[(i + 2)..];
    }

    static string Abbreviate(string address)
    {
        foreach (var (full, abbr) in new[] { ("Street", "St"), ("Road", "Rd"), ("Avenue", "Ave"), ("Drive", "Dr"), ("Court", "Ct"),
                     ("Place", "Pl"), ("Crescent", "Cres"), ("Parade", "Pde"), ("Lane", "Ln") })
            address = address.Replace(full, abbr);
        return address.ToUpperInvariant();
    }

    DateOnly Payday(int weekday)
    {
        var d = historyStart;
        while ((int)d.DayOfWeek != weekday) d = d.AddDays(1);
        return d.AddDays(f.Random.Bool() ? 7 : 0);
    }

    T Weighted<T>(params (T Value, double Weight)[] options)
    {
        var roll = f.Random.Double() * options.Sum(o => o.Weight);
        foreach (var (value, weight) in options)
            if ((roll -= weight) < 0) return value;
        return options[^1].Value;
    }

    double Normal(double mean, double sd) =>
        mean + sd * Math.Sqrt(-2 * Math.Log(1 - f.Random.Double())) * Math.Cos(2 * Math.PI * f.Random.Double());

    decimal LogNormal(double median, double spread) => (decimal)(median * Math.Exp(Normal(0, spread)));

    DateOnly RandomDate(DateOnly from, DateOnly to) => from >= to ? from : from.AddDays(f.Random.Int(0, to.DayNumber - from.DayNumber));

    // Skewed towards recent dates: tenure follows an exponential curve with the given mean
    DateOnly RecentDate(DateOnly earliest, double meanYears, DateOnly latest)
    {
        var span = latest.DayNumber - earliest.DayNumber;
        if (span <= 0) return latest;
        return latest.AddDays(-(int)Math.Min(span, -Math.Log(1 - f.Random.Double()) * meanYears * 365));
    }

    static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    static int MonthsBetween(DateOnly a, DateOnly b) => (b.Year - a.Year) * 12 + b.Month - a.Month - (b.Day < a.Day ? 1 : 0);
}
