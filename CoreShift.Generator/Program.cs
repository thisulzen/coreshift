using Bogus;

var faker = new Faker("en_AU");

var accounts = Enumerable.Range(1, 10).Select(i => new LegacyAccount(
    AcctNo: $"LG-{faker.Random.Number(100000, 999999)}",
    CustName: MessyName(faker.Name.FullName()),
    OpenDate: faker.Date.Past(20).ToString("dd/MM/yyyy"),
    // ~3% overdrawn
    Balance: faker.Random.Bool(0.03f)
        ? -Math.Round(faker.Finance.Amount(1, 2000), 2)
        : Math.Round(faker.Finance.Amount(0, 50000), 2),
    Status: faker.PickRandom("OPEN", "OPEN", "OPEN", "CLOSED")
)).ToList();

foreach (var a in accounts)
    Console.WriteLine(a);

// ~5% of names typed badly into the old system: all lowercase or padded with spaces
string MessyName(string name)
{
    if (!faker.Random.Bool(0.05f)) return name;
    return faker.Random.Bool() ? name.ToLower() : $"  {name}   ";
}

record LegacyAccount(string AcctNo, string CustName,
    string OpenDate, decimal Balance, string Status);
