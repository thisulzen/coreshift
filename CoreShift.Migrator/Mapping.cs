using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoreShift.Migrator;

// The mapping lives in mapping/mapping.v1.json, not in code: which legacy column feeds which target
// column, through which named rule, plus lookup tables for codes. Every run records the version,
// so any reconciliation result can be traced back to the exact rules that produced it.
public record ColumnRule(string To, string From, string Rule = "copy");

public record MappingConfig(string Version, int CohortSize,
    Dictionary<string, Dictionary<string, string>> Maps, Dictionary<string, List<ColumnRule>> Tables)
{
    public static MappingConfig Load(string path) =>
        JsonSerializer.Deserialize<MappingConfig>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    public void Fill(DataRow row, IDataRecord legacy, string table, string? fault = null)
    {
        foreach (var r in Tables[table])
            row[r.To] = Rules.Apply(r.Rule, legacy[r.From], this, fault) ?? DBNull.Value;
    }
}

public static class Rules
{
    public static readonly string[] Names = ["copy", "text", "name", "date", "flag", "bsb", "mobile"];

    public static object? Apply(string rule, object? raw, MappingConfig config, string? fault = null)
    {
        if (raw is DBNull) raw = null;
        if (rule.StartsWith("map:"))
            return Text(raw) is not { } code ? null
                : config.Maps[rule[4..]].TryGetValue(code, out var mapped) ? mapped
                : throw new InvalidDataException($"No {rule[4..]} mapping for '{code}'");
        return rule switch
        {
            "copy" => raw,
            "text" => Text(raw),
            "name" => Name(Text(raw)),
            "date" => LegacyDate(Text(raw), fault == "date-format"),
            "flag" => Text(raw) switch { "Y" => true, "N" => false, var s => throw new InvalidDataException($"A flag must be Y or N, not '{s}'") },
            "bsb" => Bsb(Text(raw)),
            "mobile" => Mobile(Text(raw)),
            _ => throw new InvalidDataException($"Unknown mapping rule '{rule}'"),
        };
    }

    // Trimmed, and blank becomes null
    public static string? Text(object? raw) => raw switch
    {
        null or DBNull => null,
        string s => s.Trim() is { Length: > 0 } t ? t : null,
        _ => raw.ToString(),
    };

    // "paige" and "SMITH" get proper capitals. Mixed case like "McDonald" is already right, so it's left alone.
    public static string? Name(string? s) => s == null || (s != s.ToLowerInvariant() && s != s.ToUpperInvariant()) ? s
        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

    // The legacy core wrote dd/MM/yyyy, except records from before its 2010 upgrade, which kept yyyyMMdd
    public static DateTime? LegacyDate(string? s, bool monthFirstFault = false)
    {
        if (s == null) return null;
        if (!s.Contains('/')) return DateTime.ParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture);
        // Fault injection: the classic migration bug of reading 03/04 as 4 March instead of 3 April
        if (monthFirstFault && DateTime.TryParseExact(s, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wrong)) return wrong;
        return DateTime.ParseExact(s, "dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    // "994001" and "994-001" are the same BSB; the target always stores the dash
    public static string? Bsb(string? s) => s == null ? null
        : Regex.IsMatch(s, @"^\d{3}-?\d{3}$") ? $"{s[..3]}-{s[^3..]}"
        : throw new InvalidDataException($"Not a BSB: '{s}'");

    // Three legacy spellings of one mobile number become one: +61 and nine digits
    public static string? Mobile(string? s)
    {
        if (s == null) return null;
        var digits = Regex.Replace(s, @"\D", "");
        if (digits.StartsWith("61")) digits = "0" + digits[2..];
        return Regex.IsMatch(digits, @"^04\d{8}$") ? "+61" + digits[1..] : throw new InvalidDataException($"Not an Australian mobile: '{s}'");
    }
}
