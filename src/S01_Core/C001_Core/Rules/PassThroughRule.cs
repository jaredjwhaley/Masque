namespace Masque.Core.Rules;

/// <summary>Leaves values unchanged. For categories that carry no identity, such as money or status codes.</summary>
/// <param name="category">The category to pass through.</param>
public sealed class PassThroughRule(SensitiveDataType category) : ITokenRule
{
    /// <inheritdoc />
    public SensitiveDataType Category { get; } = category;

    /// <inheritdoc />
    public TokenRuleKind Kind => TokenRuleKind.PassThrough;

    /// <inheritdoc />
    public string Normalize(string raw) => raw;

    /// <inheritdoc />
    public string CreateToken(string normalized, IRandomSource random, int attempt) => normalized;
}
