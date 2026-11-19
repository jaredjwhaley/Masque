using System.Runtime.Versioning;
using System.Security.Principal;

namespace Masque.Core.Policy;

/// <summary>Who is calling Masque, as a policy sees it.</summary>
/// <param name="Name">The caller's name, such as <c>CONTOSO\pat</c> or <c>assistant</c>.</param>
/// <param name="Groups">The groups the caller belongs to.</param>
public sealed record CallerIdentity(string Name, IReadOnlyList<string> Groups);

/// <summary>
/// Tells Masque who is calling, so the policy can pick their grant. Swap the implementation to match how an
/// organisation identifies people: a fixed name for one app or tool, the Windows sign-in, or a web app's user.
/// </summary>
public interface ICallerIdentityProvider
{
    /// <summary>The caller for the current operation.</summary>
    /// <returns>The caller's identity.</returns>
    CallerIdentity Current();
}

/// <summary>Always the same caller: for a tool or service that acts as one identity, such as the MCP server.</summary>
/// <param name="name">The caller's name.</param>
/// <param name="groups">The caller's groups, if any.</param>
public sealed class FixedCallerIdentityProvider(string name, IReadOnlyList<string>? groups = null) : ICallerIdentityProvider
{
    private readonly CallerIdentity _identity = new(
        string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A caller needs a name.", nameof(name)) : name,
        groups ?? []);

    /// <inheritdoc />
    public CallerIdentity Current() => _identity;
}

/// <summary>
/// The signed-in Windows user and their groups (including Active Directory groups on a domain), so a domain
/// administrator can grant access by group in the policy.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCallerIdentityProvider : ICallerIdentityProvider
{
    /// <inheritdoc />
    public CallerIdentity Current()
    {
        using var identity = WindowsIdentity.GetCurrent();

        var groups = identity.Groups?
            .Select(TryTranslate)
            .OfType<string>()
            .ToList() ?? [];

        return new CallerIdentity(identity.Name, groups);
    }

    /// <summary>Turns a group's security identifier into its name; groups that can't be resolved are skipped.</summary>
    private static string? TryTranslate(IdentityReference group)
    {
        try
        {
            return group.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (SystemException)
        {
            // The domain controller can be unreachable; a group we can't name grants nothing (fail closed).
            return null;
        }
    }
}
