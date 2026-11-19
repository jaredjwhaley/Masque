namespace Masque.Core.Detection;

/// <summary>
/// Recognises one <see cref="SensitiveDataType"/>. The <see cref="ColumnClassifier"/> asks every registered detector
/// about a column and weighs their votes, so a new type is supported by adding a detector, not by editing the
/// classifier (the Open/Closed Principle). Implementations must never quote a value in a vote's reason.
/// </summary>
public interface ISensitiveDataDetector
{
    /// <summary>The type this detector recognises.</summary>
    SensitiveDataType Type { get; }

    /// <summary>Votes on a column from its header text alone.</summary>
    /// <param name="header">The column header; may be empty.</param>
    /// <returns>A vote for <see cref="Type"/>, or <see langword="null"/> when the header says nothing about it.</returns>
    ColumnVote? CheckHeader(string header);

    /// <summary>Votes on a column from a sample of its values.</summary>
    /// <param name="values">Non-blank, trimmed sample values; never empty.</param>
    /// <returns>A vote for <see cref="Type"/>, or <see langword="null"/> when the values don't fit it.</returns>
    ColumnVote? CheckValues(IReadOnlyList<string> values);

    /// <summary>
    /// Whether the values are consistent with this type. Pass-through types (money, codes) use it to refuse values
    /// that would leak if passed through; other types allow anything.
    /// </summary>
    /// <param name="values">Non-blank, trimmed sample values; never empty.</param>
    /// <returns><see langword="false"/> when a vote for this type must be ignored for these values.</returns>
    bool ValuesAllow(IReadOnlyList<string> values);

    /// <summary>Whether one value on its own has this type's distinctive shape (an SSN, an email address).</summary>
    /// <param name="value">One value, possibly with surrounding spaces.</param>
    /// <returns><see langword="true"/> only for shapes specific enough to flag a single value.</returns>
    bool MatchesValue(string value);
}
