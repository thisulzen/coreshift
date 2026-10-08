using Bogus;

var faker = new Faker("en_AU");

var accounts = Enumerable.Range(1, 10).Select(i => new LegacyAccount(
    AcctNo: $"LG-{faker.Random.Number(100000, 999999)}",
    CustName: faker.Name.FullName(),
    OpenDate: faker.Date.Past(20).ToString("dd/MM/yyyy"),
    Balance: Math.Round(faker.Finance.Amount(0, 50000), 2),
    Status: faker.PickRandom("OPEN", "OPEN", "OPEN", "CLOSED")
)).ToList();

foreach (var a in accounts)
    Console.WriteLine(a);

record LegacyAccount(string AcctNo, string CustName,
    string OpenDate, decimal Balance, string Status);
