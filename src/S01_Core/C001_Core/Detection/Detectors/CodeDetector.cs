namespace Masque.Core.Detection;

/// <summary>
/// Recognises non-identifying codes (status, type, frequency) by header words. Codes pass through unmasked, so a
/// code vote only counts for short values with few distinct entries: long or varied text cannot pose as a code.
/// </summary>
public sealed class CodeDetector() : SensitiveDataDetector(
    SensitiveDataType.Code,
    headerKeywords: ["code", "status", "type", "frequency", "freq", "state", "gender", "sex", "flag", "category"])
{
    private const int MaxCodeLength = 20;
    private const int MinDistinctCodesAllowed = 10;

    /// <inheritdoc />
    public override bool ValuesAllow(IReadOnlyList<string> values) => LooksLikeCode(values);

    /// <summary>
    /// True when every non-blank value is a short code with no email-like text, and there are few distinct values,
    /// as in a status or frequency column.
    /// </summary>
    /// <param name="values">Sample values; blanks are ignored.</param>
    /// <returns>Whether the values can safely pass through as codes.</returns>
    public static bool LooksLikeCode(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var nonBlank = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        if (nonBlank.Count == 0 || nonBlank.Any(v => v.Length > MaxCodeLength || v.Contains('@')))
        {
            return false;
        }

        var distinct = nonBlank.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return distinct <= Math.Max(MinDistinctCodesAllowed, nonBlank.Count / 5);
    }
}
