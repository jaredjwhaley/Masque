using Masque.Core.Vault;

namespace Masque.Core.Tests.Vault;

/// <summary>
/// Behaviour every <see cref="ITokenVault"/> must share. Each vault's test class inherits these tests.
/// </summary>
public abstract class TokenVaultContractTests
{
    /// <summary>The vault under test, fresh for each test.</summary>
    protected abstract ITokenVault Vault { get; }

    [Fact]
    public void Add_ThenLookUpBothWays_ReturnsThePair()
    {
        Vault.Add(PiiCategory.Ssn, "123456789", "900-11-2222");

        Vault.TryGetToken(PiiCategory.Ssn, "123456789", out var token).ShouldBeTrue();
        token.ShouldBe("900-11-2222");
        Vault.TryGetOriginal(PiiCategory.Ssn, "900-11-2222", out var original).ShouldBeTrue();
        original.ShouldBe("123456789");
    }

    [Fact]
    public void Lookups_UnknownValueOrToken_ReturnFalse()
    {
        Vault.TryGetToken(PiiCategory.Ssn, "123456789", out _).ShouldBeFalse();
        Vault.TryGetOriginal(PiiCategory.Ssn, "900-11-2222", out _).ShouldBeFalse();
        Vault.ContainsToken(PiiCategory.Ssn, "900-11-2222").ShouldBeFalse();
    }

    [Fact]
    public void Lookups_AreScopedByCategory()
    {
        Vault.Add(PiiCategory.Ssn, "123456789", "900-11-2222");

        Vault.TryGetToken(PiiCategory.Phone, "123456789", out _).ShouldBeFalse();
        Vault.ContainsToken(PiiCategory.Phone, "900-11-2222").ShouldBeFalse();
    }

    [Fact]
    public void ContainsToken_AfterAdd_IsTrue()
    {
        Vault.Add(PiiCategory.Ssn, "123456789", "900-11-2222");

        Vault.ContainsToken(PiiCategory.Ssn, "900-11-2222").ShouldBeTrue();
    }

    [Fact]
    public void Add_SameValueTwice_Throws()
    {
        Vault.Add(PiiCategory.Ssn, "123456789", "900-11-2222");

        Should.Throw<InvalidOperationException>(() => Vault.Add(PiiCategory.Ssn, "123456789", "900-33-4444"));
    }

    [Fact]
    public void Add_SameTokenTwice_Throws()
    {
        Vault.Add(PiiCategory.Ssn, "123456789", "900-11-2222");

        Should.Throw<InvalidOperationException>(() => Vault.Add(PiiCategory.Ssn, "987654321", "900-11-2222"));
    }

    [Fact]
    public void Add_NonAsciiValue_RoundTrips()
    {
        Vault.Add(PiiCategory.Name, "JOSÉ NÚÑEZ 李", "Avery Brooks");

        Vault.TryGetOriginal(PiiCategory.Name, "Avery Brooks", out var original).ShouldBeTrue();
        original.ShouldBe("JOSÉ NÚÑEZ 李");
    }
}
