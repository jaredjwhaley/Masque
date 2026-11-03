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

        var first = tokenizer.Tokenize(PiiCategory.Ssn, "123-45-6789");
        var second = tokenizer.Tokenize(PiiCategory.Ssn, "123-45-6789");

        second.ShouldBe(first);
    }

    [Fact]
    public void Tokenize_SameValueAcrossTokenizerInstances_ReturnsSameToken()
    {
        var first = CreateDefault().Tokenize(PiiCategory.Email, "pat@example.com");
        var second = Tokenizer.CreateDefault(_vault, new SeededRandomSource(seed: 7))
            .Tokenize(PiiCategory.Email, "pat@example.com");

        second.ShouldBe(first);
    }

    [Fact]
    public void Tokenize_SsnWithAndWithoutDashes_ReturnsSameToken()
    {
        var tokenizer = CreateDefault();

        tokenizer.Tokenize(PiiCategory.Ssn, "123456789")
            .ShouldBe(tokenizer.Tokenize(PiiCategory.Ssn, "123-45-6789"));
    }

    [Fact]
    public void Tokenize_DifferentValues_ReturnDifferentTokens()
    {
        var tokenizer = CreateDefault();

        tokenizer.Tokenize(PiiCategory.Ssn, "123-45-6789")
            .ShouldNotBe(tokenizer.Tokenize(PiiCategory.Ssn, "987-65-4321"));
    }

    [Fact]
    public void Tokenize_VaultedValue_NeverReturnsTheOriginal()
    {
        var token = CreateDefault().Tokenize(PiiCategory.Name, "Pat Smith");

        token.ShouldNotBe("Pat Smith");
    }

    [Fact]
    public void Tokenize_NewToken_IsStoredAndReversible()
    {
        var token = CreateDefault().Tokenize(PiiCategory.Ssn, "123-45-6789");

        _vault.TryGetOriginal(PiiCategory.Ssn, token, out var original).ShouldBeTrue();
        original.ShouldBe("123456789");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Tokenize_NullOrBlank_ReturnsInputUnchanged(string? value)
    {
        CreateDefault().Tokenize(PiiCategory.Ssn, value).ShouldBe(value);
    }

    [Theory]
    [InlineData(PiiCategory.Safe, "Active")]
    [InlineData(PiiCategory.Money, "1,234.56")]
    [InlineData(PiiCategory.Code, "BW")]
    public void Tokenize_PassThroughCategory_ReturnsInputUnchanged(PiiCategory category, string value)
    {
        CreateDefault().Tokenize(category, value).ShouldBe(value);
    }

    [Fact]
    public void Tokenize_CategoryWithoutSpecificRule_RedactsWithConsistentPlaceholder()
    {
        var tokenizer = CreateDefault();

        var token = tokenizer.Tokenize(PiiCategory.Address, "1 Main St");

        token.ShouldMatch(@"^\[ADDRESS-[0-9A-F]{8}\]$");
        tokenizer.Tokenize(PiiCategory.Address, "1 Main St").ShouldBe(token);
    }

    [Fact]
    public void Tokenize_GeneratedTokenAlreadyUsed_RetriesUntilUnique()
    {
        var tokenizer = new Tokenizer(_vault, new SeededRandomSource(),
            [new ScriptedRule(PiiCategory.Identifier, "A1", "A1", "B2")]);

        tokenizer.Tokenize(PiiCategory.Identifier, "first").ShouldBe("A1");
        tokenizer.Tokenize(PiiCategory.Identifier, "second").ShouldBe("B2");
    }

    [Fact]
    public void Tokenize_GeneratedTokenEqualsOriginal_Retries()
    {
        var tokenizer = new Tokenizer(_vault, new SeededRandomSource(),
            [new ScriptedRule(PiiCategory.Identifier, "same", "other")]);

        tokenizer.Tokenize(PiiCategory.Identifier, "same").ShouldBe("other");
    }

    [Fact]
    public void Tokenize_NoUniqueTokenAvailable_ThrowsTokenSpaceExhausted()
    {
        var tokenizer = new Tokenizer(_vault, new SeededRandomSource(),
            [new ScriptedRule(PiiCategory.Identifier, "A1")]);
        tokenizer.Tokenize(PiiCategory.Identifier, "first");

        Should.Throw<TokenSpaceExhaustedException>(() => tokenizer.Tokenize(PiiCategory.Identifier, "second"));
    }

    [Fact]
    public void Constructor_TwoRulesForOneCategory_Throws()
    {
        Should.Throw<ArgumentException>(() => new Tokenizer(_vault, new SeededRandomSource(),
            [new ScriptedRule(PiiCategory.Name, "x"), new ScriptedRule(PiiCategory.Name, "y")]));
    }
}
