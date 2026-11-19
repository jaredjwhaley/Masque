using Masque.Core.Policy;

namespace Masque.Core.Tests.Policy;

/// <summary>
/// Pins down how a policy decides what a caller may see raw and whether it may write: rules match by user or group,
/// several matches add up, no match falls back to the default, and a request can only narrow what was granted.
/// </summary>
public class MaskingPolicyTests
{
    private static readonly MaskingPolicy Policy = new(
        Rules:
        [
            new AccessRule("hr", Users: [], Groups: ["CONTOSO\\HR"], RawTypes: [SensitiveDataType.Name], CanWrite: true),
            new AccessRule("mailroom", Users: ["contoso\\pat"], Groups: [], RawTypes: [SensitiveDataType.Address], CanWrite: false),
        ],
        Default: AccessGrant.None);

    /// <summary>The locked policy shows nothing sensitive and allows no writes.</summary>
    [Fact]
    public void Locked_GrantsNothing()
    {
        var grant = MaskingPolicy.Locked.GrantFor(new CallerIdentity("anyone", []));

        grant.RawTypes.ShouldBeEmpty();
        grant.CanWrite.ShouldBeFalse();
    }

    /// <summary>A rule naming the caller's group applies.</summary>
    [Fact]
    public void GrantFor_GroupMatch_GetsThatRule()
    {
        var grant = Policy.GrantFor(new CallerIdentity("contoso\\lee", ["CONTOSO\\HR"]));

        grant.RawTypes.ShouldBe([SensitiveDataType.Name]);
        grant.CanWrite.ShouldBeTrue();
    }

    /// <summary>A rule naming the caller applies, ignoring case.</summary>
    [Fact]
    public void GrantFor_UserMatch_IgnoresCase()
    {
        Policy.GrantFor(new CallerIdentity("CONTOSO\\Pat", [])).RawTypes.ShouldBe([SensitiveDataType.Address]);
    }

    /// <summary>Several matching rules add up: raw types are combined, and any rule may allow writes.</summary>
    [Fact]
    public void GrantFor_SeveralMatches_AddUp()
    {
        var grant = Policy.GrantFor(new CallerIdentity("contoso\\pat", ["contoso\\hr"]));

        grant.RawTypes.ShouldBe([SensitiveDataType.Name, SensitiveDataType.Address], ignoreOrder: true);
        grant.CanWrite.ShouldBeTrue();
    }

    /// <summary>A caller no rule names gets the default.</summary>
    [Fact]
    public void GrantFor_NoMatch_GetsTheDefault()
    {
        var policy = Policy with { Default = new AccessGrant([SensitiveDataType.Zip], canWrite: false) };

        policy.GrantFor(new CallerIdentity("someone", [])).RawTypes.ShouldBe([SensitiveDataType.Zip]);
    }

    /// <summary>A request can only narrow a grant: types it adds that weren't granted stay masked.</summary>
    [Fact]
    public void Narrow_KeepsOnlyTypesInBoth()
    {
        var grant = new AccessGrant([SensitiveDataType.Name, SensitiveDataType.Zip], canWrite: true);

        grant.Narrow([SensitiveDataType.Zip, SensitiveDataType.Ssn]).RawTypes.ShouldBe([SensitiveDataType.Zip]);
    }

    /// <summary>No request means no change.</summary>
    [Fact]
    public void Narrow_Null_KeepsTheGrant()
    {
        var grant = new AccessGrant([SensitiveDataType.Name], canWrite: true);

        grant.Narrow(null).ShouldBe(grant);
    }

    /// <summary>Non-sensitive types always show; sensitive ones only when granted.</summary>
    [Fact]
    public void Shows_PassThroughAlways_SensitiveOnlyWhenGranted()
    {
        var grant = new AccessGrant([SensitiveDataType.Name], canWrite: false);

        grant.Shows(SensitiveDataType.Money).ShouldBeTrue();
        grant.Shows(SensitiveDataType.Name).ShouldBeTrue();
        grant.Shows(SensitiveDataType.Ssn).ShouldBeFalse();
    }

    /// <summary>A fixed identity is what an app or the MCP server uses when it acts as one named caller.</summary>
    [Fact]
    public void FixedCallerIdentityProvider_ReturnsItsIdentity()
    {
        var caller = new FixedCallerIdentityProvider("assistant").Current();

        caller.Name.ShouldBe("assistant");
        caller.Groups.ShouldBeEmpty();
    }
}
