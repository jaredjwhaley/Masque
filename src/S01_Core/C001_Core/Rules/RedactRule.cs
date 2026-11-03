namespace Masque.Core.Rules;

/// <summary>
/// Replaces the whole value with a placeholder such as <c>[ADDRESS-1A2B3C4D]</c>. The fallback for any category
/// without a specific rule, so unknown data is hidden rather than passed through.
/// </summary>
/// <param name="category">The category to redact.</param>
public sealed class RedactRule(PiiCategory category) : ITokenRule
{
    /// <inheritdoc />
    public PiiCategory Category { get; } = category;

    /// <inheritdoc />
    public TokenRuleKind Kind => TokenRuleKind.Vaulted;

    /// <inheritdoc />
    public string Normalize(string raw) => raw.Trim();

    /// <inheritdoc />
    public string CreateToken(string normalized, IRandomSource random, int attempt) =>
        $"[{Category.ToString().ToUpperInvariant()}-{TokenText.RandomHex(random, 8)}]";
}
