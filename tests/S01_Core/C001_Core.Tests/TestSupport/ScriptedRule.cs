namespace Masque.Core.Tests.TestSupport;

/// <summary>A vaulted rule that hands out a fixed list of tokens in order, to force collisions in tests.</summary>
internal sealed class ScriptedRule(SensitiveDataType category, params string[] tokens) : ITokenRule
{
    private int _next;

    public SensitiveDataType Category { get; } = category;

    public TokenRuleKind Kind => TokenRuleKind.Vaulted;

    public string Normalize(string raw) => raw.Trim();

    public string CreateToken(string normalized, IRandomSource random, int attempt) =>
        tokens[Math.Min(_next++, tokens.Length - 1)];
}
