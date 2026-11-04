using Masque.Core.Detection;

namespace Masque.Core.Tests.Detection;

public class HeaderDetectorTests
{
    private readonly HeaderDetector _detector = new();

    [Theory]
    [InlineData("SSN", PiiCategory.Ssn)]
    [InlineData("Employee_SSN", PiiCategory.Ssn)]
    [InlineData("Social Security Number", PiiCategory.Ssn)]
    [InlineData("DOB", PiiCategory.DateOfBirth)]
    [InlineData("BirthDate", PiiCategory.DateOfBirth)]
    [InlineData("first_name", PiiCategory.Name)]
    [InlineData("LastName", PiiCategory.Name)]
    [InlineData("Name", PiiCategory.Name)]
    [InlineData("E-mail", PiiCategory.Email)]
    [InlineData("WorkEmail", PiiCategory.Email)]
    [InlineData("Phone", PiiCategory.Phone)]
    [InlineData("mobile_number", PiiCategory.Phone)]
    [InlineData("ZIP", PiiCategory.Zip)]
    [InlineData("PostalCode", PiiCategory.Zip)]
    [InlineData("Address1", PiiCategory.Address)]
    [InlineData("City", PiiCategory.Address)]
    [InlineData("EmployeeID", PiiCategory.Identifier)]
    [InlineData("member_no", PiiCategory.Identifier)]
    [InlineData("AccountNumber", PiiCategory.Identifier)]
    [InlineData("HireDate", PiiCategory.Date)]
    [InlineData("GrossPay", PiiCategory.Money)]
    [InlineData("YTD_Amount", PiiCategory.Money)]
    [InlineData("Status", PiiCategory.Code)]
    [InlineData("PayFrequency", PiiCategory.Code)]
    public void Detect_KnownHeader_VotesForItsCategory(string header, PiiCategory expected)
    {
        _detector.Detect(header).Select(v => v.Category).ShouldContain(expected);
    }

    [Theory]
    [InlineData("Notes")]
    [InlineData("Col7")]
    [InlineData("")]
    public void Detect_UnknownHeader_GivesNoVotes(string header)
    {
        _detector.Detect(header).ShouldBeEmpty();
    }

    [Fact]
    public void Detect_CompanyName_IsNotAPersonName()
    {
        _detector.Detect("CompanyName").Select(v => v.Category).ShouldNotContain(PiiCategory.Name);
    }

    [Fact]
    public void Detect_Vote_ExplainsItselfWithTheHeaderOnly()
    {
        var vote = _detector.Detect("SSN").Single(v => v.Category == PiiCategory.Ssn);

        vote.Reason.ShouldContain("SSN");
        vote.Strength.ShouldBe(VoteStrength.Strong);
    }
}
