using System.Globalization;

namespace CoreShift.Generator;

// One CSV per table, the way the old core's nightly extract writes them:
// UPPER_SNAKE headers, Y/N flags, blanks for nulls.
public static class Csv
{
    public static void Write<T>(string path, IEnumerable<T> rows)
    {
        var props = typeof(T).GetProperties();
        using var w = new StreamWriter(path);
        w.WriteLine(string.Join(",", props.Select(p => Header(p.Name))));
        foreach (var row in rows)
            w.WriteLine(string.Join(",", props.Select(p => Field(p.GetValue(row)))));
    }

    static string Header(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    static string Field(object? value) => value switch
    {
        null => "",
        bool b => b ? "Y" : "N",
        decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
        // Quote anything with a comma or quote, and padded values so the spaces survive
        string s when s.IndexOfAny([',', '"']) >= 0 || s != s.Trim() => $"\"{s.Replace("\"", "\"\"")}\"",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}
