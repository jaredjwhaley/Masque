using System.Text.RegularExpressions;

namespace Masque.Core.Rules;

/// <summary>
/// ZIP codes keep only their first 3 digits (<c>75201</c> becomes <c>752**</c>), per the HIPAA Safe Harbor rule.
/// Prefixes covering 20,000 people or fewer become <c>000**</c>; anything that isn't a ZIP becomes <c>*****</c>.
/// </summary>
public sealed partial class ZipRule : ITokenRule
{
    /// <summary>
    /// Small-population 3-digit prefixes. Union of the 2000 and 2010 Census lists, so a prefix that appears on
    /// either is masked. Safe Harbor asks for current Census data; refresh this list when a newer one is published.
    /// </summary>
    private static readonly HashSet<string> RestrictedPrefixes =
    [
        "036", "059", "063", "102", "203", "205", "369", "556", "692", "790",
        "821", "823", "830", "831", "878", "879", "884", "890", "893",
    ];

    /// <inheritdoc />
    public SensitiveDataType Category => SensitiveDataType.Zip;

    /// <inheritdoc />
    public TokenRuleKind Kind => TokenRuleKind.Transform;

    /// <inheritdoc />
    public string Normalize(string raw) => raw.Trim();

    /// <inheritdoc />
    public string CreateToken(string normalized, IRandomSource random, int attempt)
    {
        var match = ZipPattern().Match(normalized);
        if (!match.Success)
        {
            return "*****";
        }

        var prefix = match.Groups["prefix"].Value;
        return RestrictedPrefixes.Contains(prefix) ? "000**" : $"{prefix}**";
    }

    /// <summary>Five-digit ZIP, optionally followed by a ZIP+4 extension.</summary>
    [GeneratedRegex(@"^(?<prefix>\d{3})\d{2}(-\d{4})?$")]
    private static partial Regex ZipPattern();
}
