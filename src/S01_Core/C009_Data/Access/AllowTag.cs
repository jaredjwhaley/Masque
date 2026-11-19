using System.Text.RegularExpressions;
using Masque.Core;

namespace Masque.Data.Access;

/// <summary>
/// Reads <c>-- masque: allow Name, Email</c> comments, which let a caller that only sends SQL text (a tool, an ORM's
/// query tag) ask for fewer raw types on one query. A tag can only narrow what the policy allows, never widen it.
/// </summary>
internal static partial class AllowTag
{
    /// <summary>The raw types the SQL's tags ask for, or <see langword="null"/> when it has none.</summary>
    /// <param name="sql">The command text.</param>
    /// <returns>
    /// The types named by every tag (intersected when there are several). Unknown names count for nothing, so a tag
    /// naming only unknown types asks for no raw types at all.
    /// </returns>
    public static IReadOnlySet<SensitiveDataType>? Parse(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return null;
        }

        HashSet<SensitiveDataType>? allowed = null;
        foreach (Match match in TagPattern().Matches(sql))
        {
            var named = match.Groups["types"].Value
                .Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseType)
                .OfType<SensitiveDataType>()
                .ToHashSet();

            if (allowed is null)
            {
                allowed = named;
            }
            else
            {
                allowed.IntersectWith(named);
            }
        }

        return allowed;
    }

    /// <summary>Reads one type name; numbers and unknown names are ignored so a tag can't name a type by accident.</summary>
    private static SensitiveDataType? ParseType(string name) =>
        !name.All(char.IsDigit) && Enum.TryParse<SensitiveDataType>(name, ignoreCase: true, out var type) && Enum.IsDefined(type)
            ? type
            : null;

    [GeneratedRegex(@"--[ \t]*masque:[ \t]*allow\b(?<types>[^\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
}
