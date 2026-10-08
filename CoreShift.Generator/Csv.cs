using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CoreShift.Generator;

// One CSV per table, the way the old core's nightly extract writes them:
// UPPER_SNAKE headers, Y/N flags, blanks for nulls. Rows are appended as they're generated.
public sealed class CsvFile<T> : IDisposable
{
    static readonly PropertyInfo[] Props = typeof(T).GetProperties();
    readonly StreamWriter writer;

    public CsvFile(string path)
    {
        writer = new StreamWriter(path);
        writer.WriteLine(string.Join(",", Props.Select(p => JsonNamingPolicy.SnakeCaseUpper.ConvertName(p.Name))));
    }

    public void WriteAll(IEnumerable<T> rows)
    {
        foreach (var row in rows)
            writer.WriteLine(string.Join(",", Props.Select(p => Field(p.GetValue(row)))));
    }

    public void Dispose() => writer.Dispose();

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
