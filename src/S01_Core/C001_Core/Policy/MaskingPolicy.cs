namespace Masque.Core.Policy;

/// <summary>
/// One entry in a policy: who it applies to and what they get. It applies when the caller's name is one of
/// <paramref name="Users"/> or the caller belongs to one of <paramref name="Groups"/> (case-insensitive).
/// </summary>
/// <param name="Name">A label for people reading the policy.</param>
/// <param name="Users">Caller names, such as <c>CONTOSO\pat</c> or <c>assistant</c>.</param>
/// <param name="Groups">Group names, such as <c>CONTOSO\HR</c>.</param>
/// <param name="RawTypes">Sensitive types shown as real values.</param>
/// <param name="CanWrite">Whether INSERT, UPDATE and DELETE are allowed.</param>
public sealed record AccessRule(
    string Name,
    IReadOnlyList<string> Users,
    IReadOnlyList<string> Groups,
    IReadOnlyList<SensitiveDataType> RawTypes,
    bool CanWrite)
{
    /// <summary>Whether this rule names the caller or one of the caller's groups.</summary>
    /// <param name="caller">Who is calling.</param>
    /// <returns><see langword="true"/> when the rule applies.</returns>
    public bool AppliesTo(CallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        return Users.Contains(caller.Name, StringComparer.OrdinalIgnoreCase)
            || Groups.Any(group => caller.Groups.Contains(group, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The grant this rule gives.</summary>
    /// <returns>The rule's raw types and write permission.</returns>
    public AccessGrant ToGrant() => new(RawTypes, CanWrite);
}

/// <summary>
/// The owner's decision about who sees what: a business gate. It is chosen by who the caller is, never by what a
/// query asks for, so a caller can't widen their own access. Several matching rules add up; a caller no rule names
/// gets <paramref name="Default"/>.
/// </summary>
/// <param name="Rules">The rules, in any order.</param>
/// <param name="Default">The grant for callers no rule names.</param>
public sealed record MaskingPolicy(IReadOnlyList<AccessRule> Rules, AccessGrant Default)
{
    /// <summary>No rules and nothing granted: everything sensitive is masked and nothing may be written.</summary>
    public static MaskingPolicy Locked { get; } = new([], AccessGrant.None);

    /// <summary>Works out what a caller may do.</summary>
    /// <param name="caller">Who is calling.</param>
    /// <returns>The combined grant of every rule that applies, or <see cref="Default"/> when none does.</returns>
    public AccessGrant GrantFor(CallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var matching = Rules.Where(rule => rule.AppliesTo(caller)).Select(rule => rule.ToGrant()).ToList();
        return matching.Count == 0 ? Default : AccessGrant.Combine(matching);
    }
}
