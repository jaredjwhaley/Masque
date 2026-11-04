using System.Text;
using System.Text.RegularExpressions;

namespace Masque.Core.Detection;

/// <summary>Turns header text into comparable forms.</summary>
internal static partial class HeaderText
{
    /// <summary>Lowercase letters and digits only: "First_Name" and "first name" both become "firstname".</summary>
    public static string Normalize(string header)
    {
        var builder = new StringBuilder(header.Length);
        foreach (var c in header)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>Splits a header into lowercase words at punctuation, spaces and camelCase: "EmployeeID" gives "employee", "id".</summary>
    public static IReadOnlyList<string> Words(string header) =>
        WordPattern().Matches(header).Select(m => m.Value.ToLowerInvariant()).ToList();

    // An acronym run (not followed by a lowercase letter), a capitalized or lowercase word, or a run of digits.
    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+")]
    private static partial Regex WordPattern();
}
