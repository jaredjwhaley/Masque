using System.Globalization;
using System.Text.RegularExpressions;

namespace Masque.Core.Detection;

/// <summary>
/// Votes on a column's category from the shape of a sample of its values. A shape counts when at least
/// 90% of the non-blank samples match it. Reasons never quote a value.
/// </summary>
public sealed partial class ValuePatternDetector
{
    /// <summary>Share of non-blank samples that must match a shape for it to count.</summary>
    public const double MatchThreshold = 0.9;

    private const int MaxCodeLength = 20;
    private const int MinDistinctCodesAllowed = 10;

    /// <summary>Returns votes for every shape the samples match.</summary>
    /// <param name="samples">Sampled values of one column; blanks are ignored.</param>
    /// <returns>Strong votes for specific shapes, or a weak Identifier vote for plain numbers.</returns>
    public IReadOnlyList<ColumnVote> Detect(IReadOnlyList<string?> samples)
    {
        var values = NonBlank(samples);
        if (values.Count == 0)
        {
            return [];
        }

        var votes = new List<ColumnVote>();
        AddIfShared(votes, values, SsnPattern(), PiiCategory.Ssn, "SSNs", VoteStrength.Strong);
        AddIfShared(votes, values, EmailPattern(), PiiCategory.Email, "email addresses", VoteStrength.Strong);
        AddIfShared(votes, values, PhonePattern(), PiiCategory.Phone, "phone numbers", VoteStrength.Strong);
        AddIfShared(votes, values, DatePattern(), PiiCategory.Date, "dates", VoteStrength.Strong);
        AddIfShared(votes, values, ZipPlusFourPattern(), PiiCategory.Zip, "ZIP+4 codes", VoteStrength.Strong);

        if (votes.Count == 0)
        {
            AddIfShared(votes, values, NumberPattern(), PiiCategory.Identifier, "numbers", VoteStrength.Weak);
        }

        return votes;
    }

    /// <summary>True when every non-blank value reads as an amount: digits with optional $, commas, sign or parentheses.</summary>
    /// <param name="samples">Sampled values of one column.</param>
    /// <returns>Whether the column can safely pass through as money.</returns>
    public static bool LooksLikeMoney(IReadOnlyList<string?> samples)
    {
        var values = NonBlank(samples);
        return values.Count > 0 && values.All(v => MoneyPattern().IsMatch(v));
    }

    /// <summary>
    /// True when every non-blank value is a short code with no email-like text, and there are few distinct values,
    /// as in a status or frequency column. Long or varied text fails, so free text can't pass as a code.
    /// </summary>
    /// <param name="samples">Sampled values of one column.</param>
    /// <returns>Whether the column can safely pass through as a code.</returns>
    public static bool LooksLikeCode(IReadOnlyList<string?> samples)
    {
        var values = NonBlank(samples);
        if (values.Count == 0 || values.Any(v => v.Length > MaxCodeLength || v.Contains('@')))
        {
            return false;
        }

        var distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return distinct <= Math.Max(MinDistinctCodesAllowed, values.Count / 5);
    }

    /// <summary>True when a single value has a specific personal-data shape, for spotting a data row used as headers.</summary>
    /// <param name="value">One cell.</param>
    /// <returns>Whether the cell looks like an SSN, email, phone number or date.</returns>
    public static bool LooksLikePersonalValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return SsnPattern().IsMatch(trimmed) || EmailPattern().IsMatch(trimmed)
            || PhonePattern().IsMatch(trimmed) || DatePattern().IsMatch(trimmed);
    }

    private static List<string> NonBlank(IReadOnlyList<string?> samples) =>
        samples.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();

    private static void AddIfShared(List<ColumnVote> votes, List<string> values, Regex pattern, PiiCategory category,
        string description, VoteStrength strength)
    {
        var share = (double)values.Count(v => pattern.IsMatch(v)) / values.Count;
        if (share >= MatchThreshold)
        {
            votes.Add(new ColumnVote(category, strength,
                string.Create(CultureInfo.InvariantCulture, $"{share:P0} of sampled values look like {description}")));
        }
    }

    [GeneratedRegex(@"^\d{3}-\d{2}-\d{4}$")]
    private static partial Regex SsnPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    // Needs a separator before the last 4 digits, so a bare 10-digit number isn't mistaken for a phone.
    [GeneratedRegex(@"^(\+?1[-. ]?)?\(?\d{3}\)?[-. ]?\d{3}[-. ]\d{4}$")]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"^(\d{4}-\d{1,2}-\d{1,2}|\d{1,2}/\d{1,2}/\d{2,4})([ T]\d{1,2}:\d{2}(:\d{2})?.*)?$")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^\d{5}-\d{4}$")]
    private static partial Regex ZipPlusFourPattern();

    [GeneratedRegex(@"^-?[\d,]*\.?\d+$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^\(?-?\$?\s*-?[\d,]*\.?\d+\)?$")]
    private static partial Regex MoneyPattern();
}
