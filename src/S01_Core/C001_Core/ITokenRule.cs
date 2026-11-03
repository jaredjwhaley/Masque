namespace Masque.Core;

/// <summary>How the <see cref="Tokenizer"/> treats values of a rule's category.</summary>
public enum TokenRuleKind
{
    /// <summary>The value is returned unchanged.</summary>
    PassThrough,

    /// <summary>The value is reduced by a fixed rule (for example a ZIP cut to 3 digits). No vault entry.</summary>
    Transform,

    /// <summary>The value gets a random token, stored in the vault so it is reused and reversible.</summary>
    Vaulted,
}

/// <summary>
/// De-identification rule for one <see cref="PiiCategory"/>: how to normalize a value and what its token looks like.
/// </summary>
public interface ITokenRule
{
    /// <summary>The category this rule handles. A tokenizer holds at most one rule per category.</summary>
    PiiCategory Category { get; }

    /// <summary>How the tokenizer applies this rule.</summary>
    TokenRuleKind Kind { get; }

    /// <summary>
    /// Puts a raw value in canonical form so spelling variants (dashes, case, spacing) share one token.
    /// </summary>
    /// <param name="raw">The value as it appeared in the input. Never null.</param>
    /// <returns>The canonical value, used as the vault key.</returns>
    string Normalize(string raw);

    /// <summary>Creates a candidate token. The tokenizer rejects candidates that collide and asks again.</summary>
    /// <param name="normalized">The value after <see cref="Normalize"/>.</param>
    /// <param name="random">Random source for generated parts.</param>
    /// <param name="attempt">Zero-based retry count, so a rule can widen its token space after repeated collisions.</param>
    /// <returns>The candidate token.</returns>
    string CreateToken(string normalized, IRandomSource random, int attempt);
}
