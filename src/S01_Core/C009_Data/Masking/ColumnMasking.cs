using Masque.Core;

namespace Masque.Data.Masking;

/// <summary>What Masque does with a result column's values.</summary>
public enum MaskAction
{
    /// <summary>Values are returned as the database sent them: the column isn't sensitive, or the caller may see it raw.</summary>
    PassThrough,

    /// <summary>Every value is replaced by its token.</summary>
    Tokenize,

    /// <summary>
    /// A column reviewed as not sensitive. Each value is checked and masked as free text if it looks sensitive or
    /// doesn't fit the column's type (a name in a money column, say), in case the data doesn't match its review.
    /// </summary>
    CheckValues,

    /// <summary>
    /// A computed value with no source column. Numbers pass when they don't look sensitive; text is masked as free
    /// text, because nothing says what it was computed from.
    /// </summary>
    CheckComputed,
}

/// <summary>How one result column is masked, and why. Never holds a value.</summary>
/// <param name="Name">The column's name in the result.</param>
/// <param name="Action">What happens to its values.</param>
/// <param name="Type">The type its values are tokenized or checked as.</param>
/// <param name="Reason">Why, in words safe to show.</param>
public sealed record ColumnMasking(string Name, MaskAction Action, SensitiveDataType Type, string Reason)
{
    /// <summary>Whether every value in the column is replaced by a token.</summary>
    public bool IsMasked => Action == MaskAction.Tokenize;
}
