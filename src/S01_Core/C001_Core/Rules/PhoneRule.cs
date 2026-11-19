namespace Masque.Core.Rules;

/// <summary>
/// Phone numbers become <c>0xx-xxx-xxxx</c>. North American area codes never start with 0, so a token
/// can't ring anyone.
/// </summary>
public sealed class PhoneRule : ITokenRule
{
    /// <inheritdoc />
    public SensitiveDataType Category => SensitiveDataType.Phone;

    /// <inheritdoc />
    public TokenRuleKind Kind => TokenRuleKind.Vaulted;

    /// <inheritdoc />
    public string Normalize(string raw) => TokenText.KeepDigits(raw);

    /// <inheritdoc />
    public string CreateToken(string normalized, IRandomSource random, int attempt) =>
        $"0{TokenText.RandomDigits(random, 2)}-{TokenText.RandomDigits(random, 3)}-{TokenText.RandomDigits(random, 4)}";
}
