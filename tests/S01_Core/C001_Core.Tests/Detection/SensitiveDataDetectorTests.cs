using System.Text.RegularExpressions;
using Masque.Core.Detection;

namespace Masque.Core.Tests.Detection;

/// <summary>
/// Pins down the per-type detectors: each <see cref="ISensitiveDataDetector"/> recognises one
/// <see cref="SensitiveDataType"/> from header words and value shapes, and never quotes a value in its reasons.
/// </summary>
public partial class SensitiveDataDetectorTests
{
    private static readonly IReadOnlyList<ISensitiveDataDetector> Detectors = ColumnClassifier.DefaultDetectors();

    private static List<ColumnVote> HeaderVotes(string header) =>
        Detectors.Select(d => d.CheckHeader(header)).OfType<ColumnVote>().ToList();

    private static List<ColumnVote> ValueVotes(params string[] values) =>
        Detectors.Select(d => d.CheckValues(values)).OfType<ColumnVote>().ToList();

    /// <summary>Each built-in detector owns a different type, so adding a type means adding one class.</summary>
    [Fact]
    public void DefaultDetectors_OnePerType()
    {
        Detectors.Select(d => d.Type).ShouldBeUnique();
    }

    /// <summary>Every type a column can be classified as, except the Safe and FreeText fallbacks, has a detector.</summary>
    [Fact]
    public void DefaultDetectors_CoverEveryDetectableType()
    {
        var expected = Enum.GetValues<SensitiveDataType>().Except([SensitiveDataType.Safe, SensitiveDataType.FreeText]);
        Detectors.Select(d => d.Type).ShouldBe(expected, ignoreOrder: true);
    }

    /// <summary>Common header spellings vote for the type they name.</summary>
    [Theory]
    [InlineData("SSN", SensitiveDataType.Ssn)]
    [InlineData("Employee_SSN", SensitiveDataType.Ssn)]
    [InlineData("Social Security Number", SensitiveDataType.Ssn)]
    [InlineData("DOB", SensitiveDataType.DateOfBirth)]
    [InlineData("BirthDate", SensitiveDataType.DateOfBirth)]
    [InlineData("first_name", SensitiveDataType.Name)]
    [InlineData("LastName", SensitiveDataType.Name)]
    [InlineData("Name", SensitiveDataType.Name)]
    [InlineData("E-mail", SensitiveDataType.Email)]
    [InlineData("WorkEmail", SensitiveDataType.Email)]
    [InlineData("Phone", SensitiveDataType.Phone)]
    [InlineData("mobile_number", SensitiveDataType.Phone)]
    [InlineData("ZIP", SensitiveDataType.Zip)]
    [InlineData("PostalCode", SensitiveDataType.Zip)]
    [InlineData("Address1", SensitiveDataType.Address)]
    [InlineData("City", SensitiveDataType.Address)]
    [InlineData("EmployeeID", SensitiveDataType.Identifier)]
    [InlineData("member_no", SensitiveDataType.Identifier)]
    [InlineData("AccountNumber", SensitiveDataType.Identifier)]
    [InlineData("HireDate", SensitiveDataType.Date)]
    [InlineData("GrossPay", SensitiveDataType.Money)]
    [InlineData("YTD_Amount", SensitiveDataType.Money)]
    [InlineData("Status", SensitiveDataType.Code)]
    [InlineData("PayFrequency", SensitiveDataType.Code)]
    public void CheckHeader_KnownHeader_VotesForItsType(string header, SensitiveDataType expected)
    {
        HeaderVotes(header).Select(v => v.Category).ShouldContain(expected);
    }

    /// <summary>Headers with no known word get no votes.</summary>
    [Theory]
    [InlineData("Notes")]
    [InlineData("Col7")]
    [InlineData("")]
    public void CheckHeader_UnknownHeader_GivesNoVotes(string header)
    {
        HeaderVotes(header).ShouldBeEmpty();
    }

    /// <summary>An exclusion word cancels a keyword: a company name is not a person's name.</summary>
    [Fact]
    public void CheckHeader_CompanyName_IsNotAPersonName()
    {
        HeaderVotes("CompanyName").Select(v => v.Category).ShouldNotContain(SensitiveDataType.Name);
    }

    /// <summary>A header vote is strong and explains itself with the header only.</summary>
    [Fact]
    public void CheckHeader_Vote_ExplainsItselfWithTheHeaderOnly()
    {
        var vote = HeaderVotes("SSN").Single(v => v.Category == SensitiveDataType.Ssn);
        vote.Reason.ShouldContain("SSN");
        vote.Strength.ShouldBe(VoteStrength.Strong);
    }

    /// <summary>Values with a distinctive shape vote strongly for their type.</summary>
    [Theory]
    [InlineData(SensitiveDataType.Ssn, "123-45-6789", "987-65-4321")]
    [InlineData(SensitiveDataType.Email, "pat@example.com", "lee.k@example.org")]
    [InlineData(SensitiveDataType.Phone, "(512) 555-0100", "512-555-0199")]
    [InlineData(SensitiveDataType.Date, "2026-01-31", "1/2/2025")]
    [InlineData(SensitiveDataType.Zip, "75201-1234", "10001-0001")]
    public void CheckValues_KnownShape_VotesStrongly(SensitiveDataType expected, string first, string second)
    {
        ValueVotes(first, second).ShouldContain(v => v.Category == expected && v.Strength == VoteStrength.Strong);
    }

    /// <summary>Plain numbers are only a weak hint of an identifier.</summary>
    [Fact]
    public void CheckValues_PlainNumbers_VoteWeaklyForIdentifier()
    {
        var vote = ValueVotes("1001", "1002", "75201").ShouldHaveSingleItem();
        vote.Category.ShouldBe(SensitiveDataType.Identifier);
        vote.Strength.ShouldBe(VoteStrength.Weak);
    }

    /// <summary>A shape must cover at least 90% of the values to count.</summary>
    [Fact]
    public void CheckValues_MixedShapesBelowThreshold_GiveNoStrongVote()
    {
        ValueVotes("pat@example.com", "hello", "world", "again").ShouldNotContain(v => v.Strength == VoteStrength.Strong);
    }

    /// <summary>Value reasons give a share and a description, never the values.</summary>
    [Fact]
    public void CheckValues_Reason_NeverQuotesAValue()
    {
        ValueVotes("123-45-6789").Single().Reason.ShouldNotContain("123-45-6789");
    }

    /// <summary>Money passes through only when every value reads as an amount.</summary>
    [Theory]
    [InlineData(true, "1,234.56", "-12.00", "$40", "(15.25)")]
    [InlineData(false, "12", "abc")]
    public void MoneyDetector_ValuesAllow_OnlyAmounts(bool expected, params string[] values)
    {
        new MoneyDetector().ValuesAllow(values).ShouldBe(expected);
    }

    /// <summary>Code passes through only for short, repeated values; long or email-like text can't pose as a code.</summary>
    [Theory]
    [InlineData(true, "A", "T", "A", "BW")]
    [InlineData(false, "This is a long sentence about someone")]
    [InlineData(false, "pat@example.com")]
    public void CodeDetector_ValuesAllow_ShortRepeatedValues(bool expected, params string[] values)
    {
        new CodeDetector().ValuesAllow(values).ShouldBe(expected);
    }

    /// <summary>A single value can be recognised by its shape, for checking values one at a time.</summary>
    [Theory]
    [InlineData("123-45-6789", true)]
    [InlineData("987-65-4321 ", true)]
    [InlineData("12345", false)]
    public void SsnDetector_MatchesValue_ByShape(string value, bool expected)
    {
        new SsnDetector().MatchesValue(value).ShouldBe(expected);
    }

    /// <summary>Plain numbers and ZIP codes are too common to flag one value at a time.</summary>
    [Fact]
    public void MatchesValue_PlainNumbersAndZips_AreNotFlagged()
    {
        new IdentifierDetector().MatchesValue("1001").ShouldBeFalse();
        new ZipDetector().MatchesValue("75201-1234").ShouldBeFalse();
    }

    /// <summary>A custom detector plugs into the classifier without changing it (open for extension).</summary>
    [Fact]
    public void Classifier_AcceptsACustomDetector()
    {
        var classifier = new ColumnClassifier([.. Detectors, new ChartNumberDetector()]);

        classifier.Classify(0, "Chart", ["CH000001", "CH000002"]).Category.ShouldBe(SensitiveDataType.Identifier);
    }

    /// <summary>A stand-in for a team's own detector: "chart" headers hold medical record numbers.</summary>
    private sealed partial class ChartNumberDetector() : SensitiveDataDetector(
        SensitiveDataType.Identifier, headerKeywords: ["chart"], valueShape: Shape(), shapeDescription: "chart numbers")
    {
        [GeneratedRegex(@"^CH\d{6}$")]
        private static partial Regex Shape();
    }
}
