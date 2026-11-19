using Masque.Core.Rules;
using Masque.Core.Tests.TestSupport;

namespace Masque.Core.Tests.Rules;

public class RuleTests
{
    private readonly SeededRandomSource _random = new();

    [Fact]
    public void Ssn_Normalize_KeepsDigitsOnly()
    {
        new SsnRule().Normalize(" 123-45 6789 ").ShouldBe("123456789");
    }

    [Fact]
    public void Ssn_Token_IsInTheNeverIssued900Range()
    {
        for (var i = 0; i < 200; i++)
        {
            new SsnRule().CreateToken("123456789", _random, attempt: 0).ShouldMatch(@"^9\d{2}-\d{2}-\d{4}$");
        }
    }

    [Fact]
    public void Email_Token_UsesTheReservedInvalidDomain()
    {
        new EmailRule().CreateToken("pat@example.com", _random, attempt: 0)
            .ShouldMatch(@"^user\d{6}@example\.invalid$");
    }

    [Fact]
    public void Email_Normalize_TrimsAndLowercases()
    {
        new EmailRule().Normalize("  Pat@Example.COM ").ShouldBe("pat@example.com");
    }

    [Fact]
    public void Phone_Token_StartsWithZeroSoItIsNeverARealUsNumber()
    {
        new PhoneRule().CreateToken("5125550100", _random, attempt: 0).ShouldMatch(@"^0\d{2}-\d{3}-\d{4}$");
    }

    [Fact]
    public void Phone_Normalize_KeepsDigitsOnly()
    {
        new PhoneRule().Normalize("(512) 555-0100").ShouldBe("5125550100");
    }

    [Fact]
    public void Name_Normalize_CollapsesSpacesAndUppercases()
    {
        new NameRule().Normalize("  pat   smith ").ShouldBe("PAT SMITH");
    }

    [Fact]
    public void Name_FirstAttempts_ReturnFirstAndLastName()
    {
        new NameRule().CreateToken("PAT SMITH", _random, attempt: 0).ShouldMatch(@"^[A-Z][a-z]+ [A-Z][a-z]+$");
    }

    [Fact]
    public void Name_SingleWordValue_GetsASingleWordToken()
    {
        new NameRule().CreateToken("ZEBULON", _random, attempt: 0).ShouldMatch(@"^[A-Z][a-z]+$");
    }

    [Fact]
    public void Name_SingleWordLaterAttempts_AddANumber()
    {
        new NameRule().CreateToken("ZEBULON", _random, attempt: 50).ShouldMatch(@"^[A-Z][a-z]+ \d{1,4}$");
    }

    [Fact]
    public void Name_LaterAttempts_AddANumberToWidenTheTokenSpace()
    {
        new NameRule().CreateToken("PAT SMITH", _random, attempt: 50).ShouldMatch(@"^[A-Z][a-z]+ [A-Z][a-z]+ \d{1,4}$");
    }

    [Theory]
    [InlineData("AB-1234", @"^[A-Z]{2}-\d{4}$")]
    [InlineData("emp_0042x", @"^[a-z]{3}_\d{4}[a-z]$")]
    [InlineData("000123", @"^\d{6}$")]
    public void Identifier_Token_KeepsLengthCaseAndPunctuation(string value, string pattern)
    {
        new IdentifierRule().CreateToken(value, _random, attempt: 0).ShouldMatch(pattern);
    }

    [Theory]
    [InlineData("75201", "752**")]
    [InlineData("75201-1234", "752**")]
    [InlineData(" 75201 ", "752**")]
    [InlineData("03601", "000**")]
    [InlineData("89301", "000**")]
    [InlineData("7520", "*****")]
    [InlineData("ABCDE", "*****")]
    public void Zip_KeepsThreeDigitsAndZeroesSmallPopulationAreas(string value, string expected)
    {
        var rule = new ZipRule();

        rule.CreateToken(rule.Normalize(value), _random, attempt: 0).ShouldBe(expected);
    }

    [Fact]
    public void Redact_Token_NamesTheCategoryAndHidesTheValue()
    {
        new RedactRule(SensitiveDataType.FreeText).CreateToken("anything", _random, attempt: 0)
            .ShouldMatch(@"^\[FREETEXT-[0-9A-F]{8}\]$");
    }
}
