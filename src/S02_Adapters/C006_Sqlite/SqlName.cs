using System.Text;

namespace Masque.Sqlite;

/// <summary>
/// Turns file headers and sheet names into plain SQL names (letters, digits and underscores), so an assistant
/// can write <c>SELECT First_Name FROM people</c> without quoting. Header matching in configs ignores
/// punctuation, so "First Name" and "First_Name" still share one config.
/// </summary>
internal static class SqlName
{
    /// <summary>Makes a list of names valid and unique (case-insensitively, as SQLite compares them).</summary>
    /// <param name="names">Raw names in order.</param>
    /// <param name="fallbackPrefix">Prefix for blank names, followed by the 1-based position ("Column3").</param>
    /// <returns>Sanitized names in the same order.</returns>
    public static IReadOnlyList<string> MakeUnique(IEnumerable<string?> names, string fallbackPrefix)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var position = 0;

        foreach (var raw in names)
        {
            position++;
            var name = Sanitize(raw);
            if (name.Length == 0)
            {
                name = $"{fallbackPrefix}{position}";
            }

            var candidate = name;
            for (var suffix = 2; !used.Add(candidate); suffix++)
            {
                candidate = $"{name}_{suffix}";
            }

            result.Add(candidate);
        }

        return result;
    }

    /// <summary>Quotes a name for SQLite, doubling any embedded quote.</summary>
    /// <param name="name">The name to quote.</param>
    /// <returns>The quoted name.</returns>
    public static string Quote(string name) => $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>Replaces each run of other characters with one underscore, trims underscores, and prefixes a leading digit.</summary>
    private static string Sanitize(string? raw)
    {
        var builder = new StringBuilder();
        foreach (var c in raw ?? string.Empty)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var name = builder.ToString().Trim('_');
        return name.Length > 0 && char.IsAsciiDigit(name[0]) ? $"c_{name}" : name;
    }
}
