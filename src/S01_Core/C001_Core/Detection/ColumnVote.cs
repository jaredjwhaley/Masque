namespace Masque.Core.Detection;

/// <summary>How much a vote should count when votes disagree.</summary>
public enum VoteStrength
{
    /// <summary>A generic hint, such as "the values are numbers". Used only when no strong vote exists.</summary>
    Weak,

    /// <summary>A specific signal, such as a header named "SSN" or values shaped like email addresses.</summary>
    Strong,
}

/// <summary>One detector's opinion about what a column holds.</summary>
/// <param name="Category">The category voted for.</param>
/// <param name="Strength">How specific the evidence is.</param>
/// <param name="Reason">Why, in words safe to show anyone: it may quote the header but never a value.</param>
public sealed record ColumnVote(SensitiveDataType Category, VoteStrength Strength, string Reason);

/// <summary>The decision for one column: what it holds and why.</summary>
/// <param name="Index">Zero-based column position.</param>
/// <param name="Header">The column's header text.</param>
/// <param name="Category">The category the column will be tokenized as.</param>
/// <param name="Reason">Why this category was chosen; never quotes a value.</param>
public sealed record ColumnPlan(int Index, string Header, SensitiveDataType Category, string Reason);
