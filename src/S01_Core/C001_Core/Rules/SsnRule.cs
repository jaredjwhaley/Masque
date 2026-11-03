namespace Masque.Core.Rules;

/// <summary>
/// Social Security numbers become <c>9xx-xx-xxxx</c>. The Social Security Administration never issues
/// numbers starting with 9, so a token can't belong to a real person.
/// </summary>
public sealed class SsnRule : ITokenRule
{
    /// <inheritdoc />
    public PiiCategory Category => PiiCategory.Ssn;

    /// <inheritdoc />
    public TokenRuleKind Kind => TokenRuleKind.Vaulted;

    /// <inheritdoc />
    public string Normalize(string raw) => TokenText.KeepDigits(raw);

    /// <inheritdoc />
    public string CreateToken(string normalized, IRandomSource random, int attempt) =>
        $"9{TokenText.RandomDigits(random, 2)}-{TokenText.RandomDigits(random, 2)}-{TokenText.RandomDigits(random, 4)}";
}
