using System.Text;
using Masque.Core;
using Masque.Core.Tests.TestSupport;
using Masque.Core.Tests.Vault;
using Masque.Core.Vault;
using Masque.Vault.Tests.TestSupport;

namespace Masque.Vault.Tests;

public sealed class SqliteTokenVaultTests : TokenVaultContractTests, IDisposable
{
    private readonly TempVaultFile _file = new();
    private readonly FakeKeyProtector _alice = new("alice");
    private SqliteTokenVault _vault;

    public SqliteTokenVaultTests()
    {
        _vault = SqliteTokenVault.Open(_file.Path, _alice);
    }

    protected override ITokenVault Vault => _vault;

    public void Dispose()
    {
        _vault.Dispose();
        _file.Dispose();
    }

    private void Reopen(IKeyProtector? protector = null)
    {
        _vault.Dispose();
        _vault = SqliteTokenVault.Open(_file.Path, protector ?? _alice);
    }

    [Fact]
    public void Open_NewPath_CreatesTheFileAndFolder()
    {
        File.Exists(_file.Path).ShouldBeTrue();
    }

    [Fact]
    public void Reopen_SameKey_KeepsEveryPair()
    {
        _vault.Add(PiiCategory.Ssn, "123456789", "900-11-2222");

        Reopen();

        _vault.TryGetToken(PiiCategory.Ssn, "123456789", out var token).ShouldBeTrue();
        token.ShouldBe("900-11-2222");
        _vault.TryGetOriginal(PiiCategory.Ssn, "900-11-2222", out var original).ShouldBeTrue();
        original.ShouldBe("123456789");
    }

    [Fact]
    public void Tokenizer_AcrossReopen_ReturnsTheSameToken()
    {
        var first = Tokenizer.CreateDefault(_vault, new SeededRandomSource(1)).Tokenize(PiiCategory.Name, "Pat Smith");

        Reopen();

        Tokenizer.CreateDefault(_vault, new SeededRandomSource(2)).Tokenize(PiiCategory.Name, "pat  smith").ShouldBe(first);
    }

    [Fact]
    public void Open_WithAnotherOwnersKeyProtector_ThrowsVaultAccessException()
    {
        _vault.Dispose();

        Should.Throw<VaultAccessException>(() => SqliteTokenVault.Open(_file.Path, new FakeKeyProtector("mallory")));

        _vault = SqliteTokenVault.Open(_file.Path, _alice);
    }

    [Fact]
    public void VaultFile_NeverHoldsARealValueInReadableForm()
    {
        string[] realValues = ["123456789", "PAT SMITH", "pat.smith@example.com"];
        _vault.Add(PiiCategory.Ssn, realValues[0], "900-11-2222");
        _vault.Add(PiiCategory.Name, realValues[1], "Avery Brooks");
        _vault.Add(PiiCategory.Email, realValues[2], "user000001@example.invalid");
        _vault.Dispose();

        var bytes = File.ReadAllBytes(_file.Path);

        foreach (var value in realValues)
        {
            bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)).ShouldBe(-1, $"UTF-8 '{value}' found in the vault file");
            bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(value)).ShouldBe(-1, $"UTF-16 '{value}' found in the vault file");
        }

        _vault = SqliteTokenVault.Open(_file.Path, _alice);
    }

    [Fact]
    public void SameValueInTwoVaults_IsStoredDifferently()
    {
        using var otherFile = new TempVaultFile();
        using (var other = SqliteTokenVault.Open(otherFile.Path, _alice))
        {
            other.Add(PiiCategory.Ssn, "123456789", "900-11-2222");
        }

        _vault.Add(PiiCategory.Ssn, "123456789", "900-11-2222");
        _vault.Dispose();

        // Each vault has its own random key, so a value's stored index differs between vaults
        // and one vault's file can't be used to look values up in another.
        SqliteTokenVault.ReadValueIndexes(_file.Path).Single()
            .ShouldNotBe(SqliteTokenVault.ReadValueIndexes(otherFile.Path).Single());

        _vault = SqliteTokenVault.Open(_file.Path, _alice);
    }
}
