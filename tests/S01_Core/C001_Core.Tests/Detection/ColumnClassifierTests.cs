using Masque.Core.Detection;

namespace Masque.Core.Tests.Detection;

public class ColumnClassifierTests
{
    private readonly ColumnClassifier _classifier = ColumnClassifier.CreateDefault();

    private ColumnPlan Classify(string header, params string[] samples) => _classifier.Classify(0, header, samples);

    [Fact]
    public void Classify_HeaderAndValuesAgree_UsesThatCategory()
    {
        Classify("SSN", "123-45-6789").Category.ShouldBe(PiiCategory.Ssn);
    }

    [Fact]
    public void Classify_UnknownHeaderButShapedValues_UsesTheValues()
    {
        Classify("Col7", "pat@example.com", "lee@example.org").Category.ShouldBe(PiiCategory.Email);
    }

    [Fact]
    public void Classify_NothingKnown_FailsClosedToFreeText()
    {
        Classify("Notes", "called on Tuesday", "left a message").Category.ShouldBe(PiiCategory.FreeText);
    }

    [Fact]
    public void Classify_StrongVotesConflict_MostSensitiveWins()
    {
        Classify("Phone", "123-45-6789").Category.ShouldBe(PiiCategory.Ssn);
    }

    [Fact]
    public void Classify_ZipHeaderWithPlainNumbers_StaysZip()
    {
        // The weak "numbers look like an identifier" vote must not override a specific header.
        Classify("ZIP", "75201", "10001").Category.ShouldBe(PiiCategory.Zip);
    }

    [Fact]
    public void Classify_MoneyHeaderWithNumbers_PassesThrough()
    {
        Classify("GrossPay", "1,234.56", "980.00").Category.ShouldBe(PiiCategory.Money);
    }

    [Fact]
    public void Classify_MoneyHeaderWithNonNumbers_IsMasked()
    {
        Classify("Rate", "Pat Smith", "Lee Kim").Category.ShouldBe(PiiCategory.FreeText);
    }

    [Fact]
    public void Classify_CodeHeaderWithLongText_IsMasked()
    {
        Classify("Status", "Spoke with the employee about their surgery next week").Category.ShouldBe(PiiCategory.FreeText);
    }

    [Fact]
    public void Classify_NumbersWithNoHeaderHint_AreIdentifiers()
    {
        Classify("Field_12", "1001", "1002").Category.ShouldBe(PiiCategory.Identifier);
    }

    [Fact]
    public void Classify_Plan_KeepsIndexHeaderAndReason()
    {
        var plan = _classifier.Classify(3, "SSN", ["123-45-6789"]);

        plan.Index.ShouldBe(3);
        plan.Header.ShouldBe("SSN");
        plan.Reason.ShouldNotBeNullOrWhiteSpace();
    }
}
