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
        Vault.Add(SensitiveDataType.Ssn, "123456789", "900-11-2222");

        Vault.TryGetToken(SensitiveDataType.Ssn, "123456789", out var token).ShouldBeTrue();
        token.ShouldBe("900-11-2222");
        Vault.TryGetOriginal(SensitiveDataType.Ssn, "900-11-2222", out var original).ShouldBeTrue();
        original.ShouldBe("123456789");
    }

    [Fact]
    public void Lookups_UnknownValueOrToken_ReturnFalse()
    {
        Vault.TryGetToken(SensitiveDataType.Ssn, "123456789", out _).ShouldBeFalse();
        Vault.TryGetOriginal(SensitiveDataType.Ssn, "900-11-2222", out _).ShouldBeFalse();
        Vault.ContainsToken(SensitiveDataType.Ssn, "900-11-2222").ShouldBeFalse();
    }

    [Fact]
    public void Lookups_AreScopedByCategory()
    {
        Vault.Add(SensitiveDataType.Ssn, "123456789", "900-11-2222");

        Vault.TryGetToken(SensitiveDataType.Phone, "123456789", out _).ShouldBeFalse();
        Vault.ContainsToken(SensitiveDataType.Phone, "900-11-2222").ShouldBeFalse();
    }

    [Fact]
    public void ContainsToken_AfterAdd_IsTrue()
    {
        Vault.Add(SensitiveDataType.Ssn, "123456789", "900-11-2222");

        Vault.ContainsToken(SensitiveDataType.Ssn, "900-11-2222").ShouldBeTrue();
    }

    [Fact]
    public void Add_SameValueTwice_Throws()
    {
        Vault.Add(SensitiveDataType.Ssn, "123456789", "900-11-2222");

        Should.Throw<InvalidOperationException>(() => Vault.Add(SensitiveDataType.Ssn, "123456789", "900-33-4444"));
    }

    [Fact]
    public void Add_SameTokenTwice_Throws()
    {
        Vault.Add(SensitiveDataType.Ssn, "123456789", "900-11-2222");

        Should.Throw<InvalidOperationException>(() => Vault.Add(SensitiveDataType.Ssn, "987654321", "900-11-2222"));
    }

    [Fact]
    public void Add_NonAsciiValue_RoundTrips()
    {
        Vault.Add(SensitiveDataType.Name, "JOSÉ NÚÑEZ 李", "Avery Brooks");

        Vault.TryGetOriginal(SensitiveDataType.Name, "Avery Brooks", out var original).ShouldBeTrue();
        original.ShouldBe("JOSÉ NÚÑEZ 李");
    }

    /// <summary>
    /// Each way a token's value is written in the data (its spelling before normalization) is kept once, so a filter
    /// on the token can match the value however a source stores it.
    /// </summary>
    [Fact]
    public void AddSpelling_ThenGetSpellings_ReturnsEachSpellingOnce()
    {
        Vault.Add(SensitiveDataType.Ssn, "123456789", "900-11-2222");

        Vault.AddSpelling(SensitiveDataType.Ssn, "900-11-2222", "123-45-6789");
        Vault.AddSpelling(SensitiveDataType.Ssn, "900-11-2222", "123 45 6789");
        Vault.AddSpelling(SensitiveDataType.Ssn, "900-11-2222", "123-45-6789");

        Vault.GetSpellings(SensitiveDataType.Ssn, "900-11-2222").ShouldBe(["123-45-6789", "123 45 6789"], ignoreOrder: true);
    }

    /// <summary>A token with no recorded spellings, or an unknown token, has none.</summary>
    [Fact]
    public void GetSpellings_NoneRecorded_IsEmpty()
    {
        Vault.Add(SensitiveDataType.Ssn, "123456789", "900-11-2222");

        Vault.GetSpellings(SensitiveDataType.Ssn, "900-11-2222").ShouldBeEmpty();
        Vault.GetSpellings(SensitiveDataType.Ssn, "900-99-9999").ShouldBeEmpty();
    }

    /// <summary>A spelling can only be recorded for a token the vault holds, so it can never float free of a value.</summary>
    [Fact]
    public void AddSpelling_UnknownToken_Throws()
    {
        Should.Throw<InvalidOperationException>(() => Vault.AddSpelling(SensitiveDataType.Ssn, "900-11-2222", "123-45-6789"));
    }

    /// <summary>Spellings are scoped by category, like the pairs they belong to.</summary>
    [Fact]
    public void Spellings_AreScopedByCategory()
    {
        Vault.Add(SensitiveDataType.Ssn, "123456789", "900-11-2222");
        Vault.Add(SensitiveDataType.Phone, "5551234567", "900-11-2222");

        Vault.AddSpelling(SensitiveDataType.Ssn, "900-11-2222", "123-45-6789");

        Vault.GetSpellings(SensitiveDataType.Phone, "900-11-2222").ShouldBeEmpty();
    }
}
