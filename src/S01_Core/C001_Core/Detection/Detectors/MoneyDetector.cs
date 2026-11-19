using System.Text.RegularExpressions;

namespace Masque.Core.Detection;

/// <summary>
/// Recognises amounts, hours and rates by header words. Money passes through unmasked so calculations reproduce,
/// so a money vote only counts when every value reads as an amount.
/// </summary>
public sealed partial class MoneyDetector() : SensitiveDataDetector(
    SensitiveDataType.Money,
    headerKeywords: ["amount", "amt", "pay", "wage", "wages", "salary", "rate", "hours", "hrs", "gross", "net", "tax",
        "deduction", "premium", "balance", "total", "ytd", "earning"])
{
    /// <inheritdoc />
    public override bool ValuesAllow(IReadOnlyList<string> values) => LooksLikeMoney(values);

    /// <summary>True when every non-blank value reads as an amount: digits with optional $, commas, sign or parentheses.</summary>
    /// <param name="values">Sample values; blanks are ignored.</param>
    /// <returns>Whether the values can safely pass through as money.</returns>
    public static bool LooksLikeMoney(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var nonBlank = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        return nonBlank.Count > 0 && nonBlank.All(MoneyShape().IsMatch);
    }

    [GeneratedRegex(@"^\(?-?\$?\s*-?[\d,]*\.?\d+\)?$")]
    private static partial Regex MoneyShape();
}
