namespace Masque.Core;

/// <summary>
/// Thrown when a rule can't produce an unused token, usually because its format allows too few values
/// (for example a 2-character identifier in a large file).
/// </summary>
public sealed class TokenSpaceExhaustedException : InvalidOperationException
{
    /// <summary>Creates the exception for a category that ran out of unique tokens.</summary>
    /// <param name="category">The category whose rule ran out.</param>
    /// <param name="attempts">How many candidates were tried.</param>
    public TokenSpaceExhaustedException(PiiCategory category, int attempts)
        : base($"No unused {category} token found after {attempts} attempts. The token format is too small for this data.")
    {
        Category = category;
        Attempts = attempts;
    }

    /// <summary>The category whose rule ran out of tokens.</summary>
    public PiiCategory Category { get; }

    /// <summary>How many candidates were tried.</summary>
    public int Attempts { get; }
}
