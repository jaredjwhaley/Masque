using System.Diagnostics.CodeAnalysis;
using Masque.Core.Rules;
using Masque.Core.Vault;

namespace Masque.Core;

/// <summary>
/// Replaces sensitive values with tokens. The same real value always gets the same token (through the vault),
/// so joins and counts still work on de-identified data. Not thread-safe.
/// </summary>
public sealed class Tokenizer
{
    /// <summary>How many candidates a rule may offer before the tokenizer gives up.</summary>
    public const int MaxAttempts = 100;

    private readonly ITokenVault _vault;
    private readonly IRandomSource _random;
    private readonly Dictionary<PiiCategory, ITokenRule> _rules = [];

    /// <summary>Creates a tokenizer with an explicit rule set.</summary>
    /// <param name="vault">Where value-to-token pairs are stored.</param>
    /// <param name="random">Random source for new tokens.</param>
    /// <param name="rules">At most one rule per category. Categories without a rule are redacted.</param>
    /// <exception cref="ArgumentException">Two rules share a category.</exception>
    public Tokenizer(ITokenVault vault, IRandomSource random, IEnumerable<ITokenRule> rules)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(rules);

        _vault = vault;
        _random = random;

        foreach (var rule in rules)
        {
            if (!_rules.TryAdd(rule.Category, rule))
            {
                throw new ArgumentException($"More than one rule for category {rule.Category}.", nameof(rules));
            }
        }
    }

    /// <summary>Creates a tokenizer with the built-in rules from <see cref="DefaultRules"/>.</summary>
    /// <param name="vault">Where value-to-token pairs are stored.</param>
    /// <param name="random">Random source for new tokens; use <see cref="CryptoRandomSource.Shared"/> outside tests.</param>
    /// <returns>A ready tokenizer.</returns>
    public static Tokenizer CreateDefault(ITokenVault vault, IRandomSource random) => new(vault, random, DefaultRules());

    /// <summary>The built-in rules. Categories not listed here fall back to <see cref="RedactRule"/>.</summary>
    /// <returns>A new list of rule instances.</returns>
    public static IReadOnlyList<ITokenRule> DefaultRules() =>
    [
        new PassThroughRule(PiiCategory.Safe),
        new PassThroughRule(PiiCategory.Money),
        new PassThroughRule(PiiCategory.Code),
        new SsnRule(),
        new EmailRule(),
        new PhoneRule(),
        new NameRule(),
        new IdentifierRule(),
        new ZipRule(),
    ];

    /// <summary>Returns the token for a value, creating and storing one the first time the value is seen.</summary>
    /// <param name="category">What kind of value this is.</param>
    /// <param name="value">The real value. Null, empty and whitespace-only values are returned unchanged.</param>
    /// <returns>The token, or the value itself for pass-through categories.</returns>
    /// <exception cref="TokenSpaceExhaustedException">The rule could not find an unused token.</exception>
    [return: NotNullIfNotNull(nameof(value))]
    public string? Tokenize(PiiCategory category, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var rule = GetRule(category);

        return rule.Kind switch
        {
            TokenRuleKind.PassThrough => value,
            TokenRuleKind.Transform => rule.CreateToken(rule.Normalize(value), _random, attempt: 0),
            _ => GetOrCreateVaultedToken(rule, value),
        };
    }

    /// <summary>Finds the rule for a category; unknown categories are redacted (fail closed).</summary>
    private ITokenRule GetRule(PiiCategory category)
    {
        if (!_rules.TryGetValue(category, out var rule))
        {
            rule = new RedactRule(category);
            _rules.Add(category, rule);
        }

        return rule;
    }

    /// <summary>Reuses the vault's token for the value, or generates, checks and stores a new one.</summary>
    private string GetOrCreateVaultedToken(ITokenRule rule, string value)
    {
        var normalized = rule.Normalize(value);

        if (_vault.TryGetToken(rule.Category, normalized, out var existing))
        {
            return existing;
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var candidate = rule.CreateToken(normalized, _random, attempt);

            // A token must never echo the real value or reuse a token that already stands for another value.
            if (candidate == normalized || candidate == value || _vault.ContainsToken(rule.Category, candidate))
            {
                continue;
            }

            _vault.Add(rule.Category, normalized, candidate);
            return candidate;
        }

        throw new TokenSpaceExhaustedException(rule.Category, MaxAttempts);
    }
}
