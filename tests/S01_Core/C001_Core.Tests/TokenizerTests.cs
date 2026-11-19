using Masque.Core.Tests.TestSupport;
using Masque.Core.Vault;

namespace Masque.Core.Tests;

public class TokenizerTests
{
    private readonly InMemoryTokenVault _vault = new();

    private Tokenizer CreateDefault() => Tokenizer.CreateDefault(_vault, new SeededRandomSource());

    [Fact]
    public void Tokenize_SameValueTwice_ReturnsSameToken()
    {
        var tokenizer = CreateDefault();

        var first = tokenizer.Tokenize(SensitiveDataType.Ssn, "123-45-6789");
        var second = tokenizer.Tokenize(SensitiveDataType.Ssn, "123-45-6789");

        second.ShouldBe(first);
    }

    [Fact]
    public void Tokenize_SameValueAcrossTokenizerInstances_ReturnsSameToken()
    {
        var first = CreateDefault().Tokenize(SensitiveDataType.Email, "pat@example.com");
        var second = Tokenizer.CreateDefault(_vault, new SeededRandomSource(seed: 7))
            .Tokenize(SensitiveDataType.Email, "pat@example.com");

        second.ShouldBe(first);
    }

    [Fact]
    public void Tokenize_SsnWithAndWithoutDashes_ReturnsSameToken()
    {
        var tokenizer = CreateDefault();

        tokenizer.Tokenize(SensitiveDataType.Ssn, "123456789")
            .ShouldBe(tokenizer.Tokenize(SensitiveDataType.Ssn, "123-45-6789"));
    }

    [Fact]
    public void Tokenize_DifferentValues_ReturnDifferentTokens()
    {
        var tokenizer = CreateDefault();

        tokenizer.Tokenize(SensitiveDataType.Ssn, "123-45-6789")
            .ShouldNotBe(tokenizer.Tokenize(SensitiveDataType.Ssn, "987-65-4321"));
    }

    [Fact]
    public void Tokenize_VaultedValue_NeverReturnsTheOriginal()
    {
        var token = CreateDefault().Tokenize(SensitiveDataType.Name, "Pat Smith");

        token.ShouldNotBe("Pat Smith");
    }

    [Fact]
    public void Tokenize_NewToken_IsStoredAndReversible()
    {
        var token = CreateDefault().Tokenize(SensitiveDataType.Ssn, "123-45-6789");

        _vault.TryGetOriginal(SensitiveDataType.Ssn, token, out var original).ShouldBeTrue();
        original.ShouldBe("123456789");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Tokenize_NullOrBlank_ReturnsInputUnchanged(string? value)
    {
        CreateDefault().Tokenize(SensitiveDataType.Ssn, value).ShouldBe(value);
    }

    [Theory]
    [InlineData(SensitiveDataType.Safe, "Active")]
    [InlineData(SensitiveDataType.Money, "1,234.56")]
    [InlineData(SensitiveDataType.Code, "BW")]
    public void Tokenize_PassThroughCategory_ReturnsInputUnchanged(SensitiveDataType category, string value)
    {
        CreateDefault().Tokenize(category, value).ShouldBe(value);
    }

    [Fact]
    public void Tokenize_CategoryWithoutSpecificRule_RedactsWithConsistentPlaceholder()
    {
        var tokenizer = CreateDefault();

        var token = tokenizer.Tokenize(SensitiveDataType.Address, "1 Main St");

        token.ShouldMatch(@"^\[ADDRESS-[0-9A-F]{8}\]$");
        tokenizer.Tokenize(SensitiveDataType.Address, "1 Main St").ShouldBe(token);
    }

    [Fact]
    public void Tokenize_GeneratedTokenAlreadyUsed_RetriesUntilUnique()
    {
        var tokenizer = new Tokenizer(_vault, new SeededRandomSource(),
            [new ScriptedRule(SensitiveDataType.Identifier, "A1", "A1", "B2")]);

        tokenizer.Tokenize(SensitiveDataType.Identifier, "first").ShouldBe("A1");
        tokenizer.Tokenize(SensitiveDataType.Identifier, "second").ShouldBe("B2");
    }

    [Fact]
    public void Tokenize_GeneratedTokenEqualsOriginal_Retries()
    {
        var tokenizer = new Tokenizer(_vault, new SeededRandomSource(),
            [new ScriptedRule(SensitiveDataType.Identifier, "same", "other")]);

        tokenizer.Tokenize(SensitiveDataType.Identifier, "same").ShouldBe("other");
    }

    [Fact]
    public void Tokenize_NoUniqueTokenAvailable_ThrowsTokenSpaceExhausted()
    {
        var tokenizer = new Tokenizer(_vault, new SeededRandomSource(),
            [new ScriptedRule(SensitiveDataType.Identifier, "A1")]);
        tokenizer.Tokenize(SensitiveDataType.Identifier, "first");

        Should.Throw<TokenSpaceExhaustedException>(() => tokenizer.Tokenize(SensitiveDataType.Identifier, "second"));
    }

    [Fact]
    public void Constructor_TwoRulesForOneCategory_Throws()
    {
        Should.Throw<ArgumentException>(() => new Tokenizer(_vault, new SeededRandomSource(),
            [new ScriptedRule(SensitiveDataType.Name, "x"), new ScriptedRule(SensitiveDataType.Name, "y")]));
    }
}
