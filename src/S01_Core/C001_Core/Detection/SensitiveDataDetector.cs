using System.Globalization;
using System.Text.RegularExpressions;

namespace Masque.Core.Detection;

/// <summary>
/// Base for detectors built from header keywords and an optional value shape, which covers most types. A subclass
/// only supplies its type, keywords and pattern, and overrides a member when its type needs more (money, codes).
/// </summary>
/// <remarks>
/// Header keywords of 5 or more letters match anywhere in the normalized header ("telephone" contains "phone");
/// shorter ones must be a whole word ("SSN", "Zip"), so "id" doesn't match inside "paid". An exclusion word cancels
/// the vote ("CompanyName" is not a person's name).
/// </remarks>
public abstract class SensitiveDataDetector : ISensitiveDataDetector
{
    /// <summary>Share of the sampled values that must match the value shape for it to count.</summary>
    public const double MatchThreshold = 0.9;

    private const int SubstringKeywordLength = 5;

    private readonly IReadOnlyList<string> _headerKeywords;
    private readonly IReadOnlyList<string> _headerExclusions;
    private readonly Regex? _valueShape;
    private readonly string _shapeDescription;
    private readonly bool _shapeFlagsSingleValues;

    /// <summary>Creates a detector.</summary>
    /// <param name="type">The type it recognises.</param>
    /// <param name="headerKeywords">Lowercase words that suggest the type in a header.</param>
    /// <param name="headerExclusions">Lowercase words that cancel a keyword match.</param>
    /// <param name="valueShape">Pattern a value of this type matches, or <see langword="null"/> for none.</param>
    /// <param name="shapeDescription">Plural description of the shape for vote reasons ("SSNs").</param>
    /// <param name="shapeFlagsSingleValues">
    /// Whether the shape is distinctive enough to flag one value on its own; false for common shapes such as ZIP+4.
    /// </param>
    protected SensitiveDataDetector(
        SensitiveDataType type,
        IReadOnlyList<string> headerKeywords,
        IReadOnlyList<string>? headerExclusions = null,
        Regex? valueShape = null,
        string? shapeDescription = null,
        bool shapeFlagsSingleValues = true)
    {
        ArgumentNullException.ThrowIfNull(headerKeywords);

        Type = type;
        _headerKeywords = headerKeywords;
        _headerExclusions = headerExclusions ?? [];
        _valueShape = valueShape;
        _shapeDescription = shapeDescription ?? $"{type} values";
        _shapeFlagsSingleValues = shapeFlagsSingleValues;
    }

    /// <inheritdoc />
    public SensitiveDataType Type { get; }

    /// <inheritdoc />
    public virtual ColumnVote? CheckHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var normalized = HeaderText.Normalize(header);
        var words = HeaderText.Words(header);

        var matches = _headerKeywords.Any(k => Matches(k, normalized, words))
            && !_headerExclusions.Any(k => Matches(k, normalized, words));

        return matches ? new ColumnVote(Type, VoteStrength.Strong, $"header \"{header}\" suggests {Type}") : null;
    }

    /// <inheritdoc />
    public virtual ColumnVote? CheckValues(IReadOnlyList<string> values)
    {
        if (_valueShape is null)
        {
            return null;
        }

        return VoteIfShared(values, _valueShape, _shapeDescription, VoteStrength.Strong);
    }

    /// <inheritdoc />
    public virtual bool ValuesAllow(IReadOnlyList<string> values) => true;

    /// <inheritdoc />
    public virtual bool MatchesValue(string value) =>
        _shapeFlagsSingleValues && _valueShape is not null && value is not null && _valueShape.IsMatch(value.Trim());

    /// <summary>Returns a vote when at least <see cref="MatchThreshold"/> of the values match the pattern.</summary>
    /// <param name="values">Non-blank sample values.</param>
    /// <param name="pattern">The shape to test.</param>
    /// <param name="description">Plural description for the reason.</param>
    /// <param name="strength">The vote's strength.</param>
    /// <returns>The vote, or <see langword="null"/>.</returns>
    protected ColumnVote? VoteIfShared(IReadOnlyList<string> values, Regex pattern, string description, VoteStrength strength)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(pattern);

        if (values.Count == 0)
        {
            return null;
        }

        var share = (double)values.Count(pattern.IsMatch) / values.Count;
        return share >= MatchThreshold
            ? new ColumnVote(Type, strength, string.Create(CultureInfo.InvariantCulture, $"{share:P0} of sampled values look like {description}"))
            : null;
    }

    /// <summary>Long keywords match inside the header; short ones must be a whole word.</summary>
    private static bool Matches(string keyword, string normalized, IReadOnlyList<string> words) =>
        keyword.Length >= SubstringKeywordLength ? normalized.Contains(keyword, StringComparison.Ordinal) : words.Contains(keyword);
}
