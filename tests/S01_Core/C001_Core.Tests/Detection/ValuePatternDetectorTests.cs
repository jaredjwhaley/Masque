using Masque.Core.Detection;

namespace Masque.Core.Tests.Detection;

public class ValuePatternDetectorTests
{
    private readonly ValuePatternDetector _detector = new();

    [Theory]
    [InlineData(PiiCategory.Ssn, "123-45-6789", "987-65-4321")]
    [InlineData(PiiCategory.Email, "pat@example.com", "lee.k@example.org")]
    [InlineData(PiiCategory.Phone, "(512) 555-0100", "512-555-0199")]
    [InlineData(PiiCategory.Date, "2026-01-31", "1/2/2025")]
    [InlineData(PiiCategory.Zip, "75201-1234", "10001-0001")]
    public void Detect_ValuesWithAKnownShape_VoteStrongly(PiiCategory expected, string first, string second)
    {
        var votes = _detector.Detect([first, second, "", "  "]);

        votes.ShouldContain(v => v.Category == expected && v.Strength == VoteStrength.Strong);
    }

    [Fact]
    public void Detect_PlainNumbers_VoteWeaklyForIdentifier()
    {
        var vote = _detector.Detect(["1001", "1002", "75201"]).ShouldHaveSingleItem();

        vote.Category.ShouldBe(PiiCategory.Identifier);
        vote.Strength.ShouldBe(VoteStrength.Weak);
    }

    [Fact]
    public void Detect_MixedShapesBelowThreshold_GiveNoStrongVote()
    {
        var votes = _detector.Detect(["pat@example.com", "hello", "world", "again"]);

        votes.ShouldNotContain(v => v.Strength == VoteStrength.Strong);
    }

    [Fact]
    public void Detect_AllBlank_GivesNoVotes()
    {
        _detector.Detect(["", " ", ""]).ShouldBeEmpty();
    }

    [Fact]
    public void Detect_Reason_NeverQuotesAValue()
    {
        var vote = _detector.Detect(["123-45-6789"]).Single();

        vote.Reason.ShouldNotContain("123-45-6789");
    }

    [Theory]
    [InlineData(true, "1,234.56", "-12.00", "$40", "(15.25)")]
    [InlineData(false, "12", "abc")]
    public void LooksLikeMoney_NumericValues(bool expected, params string[] values)
    {
        ValuePatternDetector.LooksLikeMoney(values).ShouldBe(expected);
    }

    [Theory]
    [InlineData(true, "A", "T", "A", "BW")]
    [InlineData(false, "This is a long sentence about someone")]
    [InlineData(false, "pat@example.com")]
    public void LooksLikeCode_ShortRepeatedValues(bool expected, params string[] values)
    {
        ValuePatternDetector.LooksLikeCode(values).ShouldBe(expected);
    }
}
