using Bogus;
using CoreShift.Generator;
using CoreShift.Migrator;

namespace CoreShift.Tests;

public class MappingRules
{
    static readonly MappingConfig Config = MappingConfig.Load(Path.Combine(AppContext.BaseDirectory, "mapping.v1.json"));

    [Theory]
    [InlineData("03/04/1980")]   // after the 2010 upgrade: dd/MM/yyyy
    [InlineData("19800403")]     // before it: yyyyMMdd
    public void Both_legacy_date_formats_read_as_3_April(string raw) =>
        Assert.Equal(new DateTime(1980, 4, 3), Rules.LegacyDate(raw));

    [Fact]
    public void The_date_fault_reads_month_first() =>
        Assert.Equal(new DateTime(1980, 3, 4), Rules.LegacyDate("03/04/1980", monthFirstFault: true));

    [Theory]
    [InlineData("994001")]
    [InlineData("994-001")]
    public void A_BSB_always_gets_its_dash(string raw) => Assert.Equal("994-001", Rules.Bsb(raw));

    [Theory]
    [InlineData("0412 345 678")]
    [InlineData("0412345678")]
    [InlineData("+61412345678")]
    public void Three_mobile_spellings_become_one(string raw) => Assert.Equal("+61412345678", Rules.Mobile(raw));

    [Theory]
    [InlineData("  Paige", "Paige")]
    [InlineData("sebastian", "Sebastian")]
    [InlineData("SMITH", "Smith")]
    [InlineData("MR", "Mr")]
    [InlineData("McDonald", "McDonald")]
    public void Names_are_trimmed_and_given_capitals(string raw, string expected) =>
        Assert.Equal(expected, Rules.Apply("name", raw, Config));

    [Fact]
    public void Blank_text_becomes_null() => Assert.Null(Rules.Apply("text", "   ", Config));

    [Fact]
    public void Codes_map_to_the_new_catalogue() => Assert.Equal("Closed", Rules.Apply("map:accountStatus", "CLOSED", Config));

    // A code nobody mapped must stop the migration, not slip through as a blank
    [Fact]
    public void An_unmapped_code_fails_loudly() =>
        Assert.Throws<InvalidDataException>(() => Rules.Apply("map:accountStatus", "SUSPENDED", Config));

    [Fact]
    public void A_flag_must_be_Y_or_N() => Assert.Throws<InvalidDataException>(() => Rules.Apply("flag", "X", Config));

    [Fact]
    public void Every_rule_in_the_mapping_file_exists()
    {
        foreach (var rule in Config.Tables.Values.SelectMany(t => t).Select(c => c.Rule))
            Assert.True(Rules.Names.Contains(rule) || (rule.StartsWith("map:") && Config.Maps.ContainsKey(rule[4..])), $"Unknown rule '{rule}'");
    }
}

public class DuplicateCustomers
{
    static Dedupe.Candidate Person(string id, string first, string last, string dob, string since = "01/01/2020", string? mobile = null) =>
        new(id, first, last, Rules.LegacyDate(dob)!.Value, "4000", "South Riley", Rules.LegacyDate(since)!.Value, mobile, null);

    // A real pair from the generated bank: initial only, two letters swapped, birthday day/month swapped
    [Fact]
    public void Initial_typo_and_swapped_birthday_still_match()
    {
        var merged = Dedupe.Find([Person("C1005074", "Adam", "Towne", "03/04/2007", "03/04/2025"), Person("C1005075", "A", "Towen", "04/03/2007", "25/09/2025")]);
        Assert.Equal("C1005074", merged["C1005075"].Survivor);
    }

    [Fact]
    public void A_couple_at_one_address_stays_two_people() =>
        Assert.Empty(Dedupe.Find([Person("C1", "John", "Smith", "01/02/1980"), Person("C2", "Mary", "Smith", "05/06/1982")]));

    [Fact]
    public void Different_birthdays_never_merge() =>
        Assert.Empty(Dedupe.Find([Person("C1", "Ivy", "Quinn", "04/05/1999"), Person("C2", "Ivy", "Quinn", "14/05/1999")]));

    [Fact]
    public void The_oldest_record_survives()
    {
        var merged = Dedupe.Find([Person("C9", "IVY", "Quinn", "04/05/1999", "01/01/2024"), Person("C2", "Ivy", "Quinn", "04/05/1999", "01/01/2010")]);
        Assert.Equal("C2", merged["C9"].Survivor);
    }

    [Theory]
    [InlineData("TOWNE", "TOWEN", 2)]
    [InlineData("SMITH", "SMITH", 0)]
    [InlineData("QUINN", "QUIN", 1)]
    public void Edit_distance(string a, string b, int edits) => Assert.Equal(edits, Dedupe.Edits(a, b));
}

public class LegacyBank
{
    // The generator's core promise: every balance is exactly the sum of its posted history
    [Fact]
    public void Every_balance_equals_its_history_in_whole_cents()
    {
        Randomizer.Seed = new Random(7);
        var bank = new LegacyBankGenerator(new DateOnly(2026, 9, 16), historyMonths: 3);
        var summary = new ExtractSummary();
        bank.Generate(300, () => summary.Add(bank.Customers, bank.Accounts, bank.Holders, bank.Transactions));
        Assert.True(summary.Passed);
    }
}
