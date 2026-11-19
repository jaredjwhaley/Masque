using System.Text;

namespace Masque.Core.Rules;

/// <summary>
/// Identifiers keep their shape: each digit becomes a random digit, each letter a random letter of the same case,
/// and punctuation stays. <c>AB-1234</c> might become <c>QX-8051</c>, so length checks and formats still pass.
/// </summary>
public sealed class IdentifierRule : ITokenRule
{
    /// <inheritdoc />
    public SensitiveDataType Category => SensitiveDataType.Identifier;

    /// <inheritdoc />
    public TokenRuleKind Kind => TokenRuleKind.Vaulted;

    /// <inheritdoc />
    public string Normalize(string raw) => raw.Trim();

    /// <inheritdoc />
    public string CreateToken(string normalized, IRandomSource random, int attempt)
    {
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            builder.Append(c switch
            {
                _ when char.IsAsciiDigit(c) => (char)('0' + random.Next(10)),
                _ when char.IsAsciiLetterUpper(c) => (char)('A' + random.Next(26)),
                _ when char.IsAsciiLetterLower(c) => (char)('a' + random.Next(26)),
                _ => c,
            });
        }

        return builder.ToString();
    }
}
