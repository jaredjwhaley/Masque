namespace Masque.Core.Rules;

/// <summary>
/// Email addresses become <c>userNNNNNN@example.invalid</c>. The <c>.invalid</c> top-level domain is reserved
/// (RFC 2606), so a token can never be delivered.
/// </summary>
public sealed class EmailRule : ITokenRule
{
    /// <inheritdoc />
    public SensitiveDataType Category => SensitiveDataType.Email;

    /// <inheritdoc />
    public TokenRuleKind Kind => TokenRuleKind.Vaulted;

    /// <inheritdoc />
    public string Normalize(string raw) => raw.Trim().ToLowerInvariant();

    /// <inheritdoc />
    public string CreateToken(string normalized, IRandomSource random, int attempt) =>
        $"user{TokenText.RandomDigits(random, 6)}@example.invalid";
}
