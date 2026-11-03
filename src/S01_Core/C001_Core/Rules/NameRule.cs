namespace Masque.Core.Rules;

/// <summary>
/// Names become a made-up "First Last" pair, so de-identified output still reads naturally.
/// After repeated collisions a number is added (for example "Avery Brooks 412") to widen the token space.
/// </summary>
public sealed class NameRule : ITokenRule
{
    /// <summary>Attempts before a number is added to the generated name.</summary>
    public const int PlainNameAttempts = 20;

    private static readonly string[] FirstNames =
    [
        "Avery", "Blake", "Cameron", "Dakota", "Emerson", "Finley", "Harper", "Hayden", "Jordan", "Kendall",
        "Logan", "Morgan", "Parker", "Quinn", "Reese", "Riley", "Rowan", "Sawyer", "Skyler", "Taylor",
        "Alex", "Casey", "Drew", "Elliot", "Frankie", "Jamie", "Jesse", "Kai", "Lane", "Marley",
    ];

    private static readonly string[] LastNames =
    [
        "Abbott", "Brooks", "Carver", "Dalton", "Ellis", "Fowler", "Garrison", "Hollis", "Ingram", "Jensen",
        "Keller", "Lawson", "Mercer", "Norris", "Oakley", "Pruitt", "Quigley", "Ramsey", "Sutton", "Tanner",
        "Underwood", "Vance", "Whitaker", "Yates", "Zeller", "Barlow", "Crane", "Dunbar", "Easton", "Fairfax",
    ];

    /// <inheritdoc />
    public PiiCategory Category => PiiCategory.Name;

    /// <inheritdoc />
    public TokenRuleKind Kind => TokenRuleKind.Vaulted;

    /// <inheritdoc />
    public string Normalize(string raw) =>
        string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    /// <inheritdoc />
    public string CreateToken(string normalized, IRandomSource random, int attempt)
    {
        var name = $"{FirstNames[random.Next(FirstNames.Length)]} {LastNames[random.Next(LastNames.Length)]}";

        // 900 plain combinations run out in large files; a suffix of 1-9999 widens the space to about 9 million.
        return attempt < PlainNameAttempts ? name : $"{name} {1 + random.Next(9999)}";
    }
}
