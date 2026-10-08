namespace CoreShift.Migrator;

// Finds people who were keyed into the legacy core twice. Two records match when they share a
// postcode and suburb, have the same date of birth (or the same with day and month swapped), have
// surnames within two edits of each other, and have first names that agree (a lone initial counts).
// The earliest record survives and the others map onto it.
public static class Dedupe
{
    public record Candidate(string Id, string First, string Last, DateTime Dob, string Postcode, string Suburb,
        DateTime Since, string? Mobile, string? Email);

    public static Dictionary<string, (string Survivor, string Reason)> Find(IEnumerable<Candidate> customers)
    {
        var merged = new Dictionary<string, (string, string)>();
        foreach (var block in customers.GroupBy(c => (c.Postcode, c.Suburb.Trim().ToUpperInvariant())))
        {
            var oldestFirst = block.OrderBy(c => c.Since).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
            // ponytail: compares every pair in a block, fine for a suburb; block on surname too if blocks grow to thousands
            for (int i = 0; i < oldestFirst.Count; i++)
            {
                if (merged.ContainsKey(oldestFirst[i].Id)) continue;
                for (int j = i + 1; j < oldestFirst.Count; j++)
                    if (!merged.ContainsKey(oldestFirst[j].Id) && Match(oldestFirst[i], oldestFirst[j]) is { } reason)
                        merged[oldestFirst[j].Id] = (oldestFirst[i].Id, reason);
            }
        }
        return merged;
    }

    // Returns why two records look like the same person, or null if they don't
    public static string? Match(Candidate a, Candidate b)
    {
        var dob = a.Dob == b.Dob ? "same date of birth"
            : a.Dob == Swap(b.Dob) ? "date of birth with day and month swapped" : null;
        string la = Norm(a.Last), lb = Norm(b.Last), fa = Norm(a.First), fb = Norm(b.First);
        var surname = la == lb ? "same surname" : Edits(la, lb) <= 2 ? "surname within two edits" : null;
        var first = fa == fb ? "same first name" : (fa.Length == 1 || fb.Length == 1) && fa[0] == fb[0] ? "first name matches an initial" : null;
        if (dob == null || surname == null || first == null) return null;

        var reasons = new List<string> { dob, surname, first };
        if (a.Mobile != null && a.Mobile == b.Mobile) reasons.Add("same mobile");
        if (a.Email != null && a.Email == b.Email) reasons.Add("same email");
        return string.Join(", ", reasons);
    }

    static DateTime Swap(DateTime d) => d.Day <= 12 ? new DateTime(d.Year, d.Day, d.Month) : d;

    static string Norm(string s) => s.Trim().ToUpperInvariant();

    // Levenshtein distance: the fewest single-letter edits that turn one word into the other
    public static int Edits(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }
}
