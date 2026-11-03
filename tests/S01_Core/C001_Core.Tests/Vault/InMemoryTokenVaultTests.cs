using Masque.Core.Vault;

namespace Masque.Core.Tests.Vault;

public class InMemoryTokenVaultTests
{
    private readonly InMemoryTokenVault _vault = new();

    [Fact]
    public void Add_ThenLookUpBothWays_ReturnsThePair()
    {
        _vault.Add(PiiCategory.Ssn, "123456789", "900-11-2222");

        _vault.TryGetToken(PiiCategory.Ssn, "123456789", out var token).ShouldBeTrue();
        token.ShouldBe("900-11-2222");
        _vault.TryGetOriginal(PiiCategory.Ssn, "900-11-2222", out var original).ShouldBeTrue();
        original.ShouldBe("123456789");
    }

    [Fact]
    public void Lookups_AreScopedByCategory()
    {
        _vault.Add(PiiCategory.Ssn, "123456789", "900-11-2222");

        _vault.TryGetToken(PiiCategory.Phone, "123456789", out _).ShouldBeFalse();
        _vault.ContainsToken(PiiCategory.Phone, "900-11-2222").ShouldBeFalse();
    }

    [Fact]
    public void ContainsToken_AfterAdd_IsTrue()
    {
        _vault.Add(PiiCategory.Ssn, "123456789", "900-11-2222");

        _vault.ContainsToken(PiiCategory.Ssn, "900-11-2222").ShouldBeTrue();
    }

    [Fact]
    public void Add_SameValueTwice_Throws()
    {
        _vault.Add(PiiCategory.Ssn, "123456789", "900-11-2222");

        Should.Throw<InvalidOperationException>(() => _vault.Add(PiiCategory.Ssn, "123456789", "900-33-4444"));
    }

    [Fact]
    public void Add_SameTokenTwice_Throws()
    {
        _vault.Add(PiiCategory.Ssn, "123456789", "900-11-2222");

        Should.Throw<InvalidOperationException>(() => _vault.Add(PiiCategory.Ssn, "987654321", "900-11-2222"));
    }
}
