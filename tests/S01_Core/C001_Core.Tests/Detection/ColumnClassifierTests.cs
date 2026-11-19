using Masque.Core.Detection;

namespace Masque.Core.Tests.Detection;

public class ColumnClassifierTests
{
    private readonly ColumnClassifier _classifier = ColumnClassifier.CreateDefault();

    private ColumnPlan Classify(string header, params string[] samples) => _classifier.Classify(0, header, samples);

    [Fact]
    public void Classify_HeaderAndValuesAgree_UsesThatCategory()
    {
        Classify("SSN", "123-45-6789").Category.ShouldBe(SensitiveDataType.Ssn);
    }

    [Fact]
    public void Classify_UnknownHeaderButShapedValues_UsesTheValues()
    {
        Classify("Col7", "pat@example.com", "lee@example.org").Category.ShouldBe(SensitiveDataType.Email);
    }

    [Fact]
    public void Classify_NothingKnown_FailsClosedToFreeText()
    {
        Classify("Notes", "called on Tuesday", "left a message").Category.ShouldBe(SensitiveDataType.FreeText);
    }

    [Fact]
    public void Classify_StrongVotesConflict_MostSensitiveWins()
    {
        Classify("Phone", "123-45-6789").Category.ShouldBe(SensitiveDataType.Ssn);
    }

    [Fact]
    public void Classify_ZipHeaderWithPlainNumbers_StaysZip()
    {
        // The weak "numbers look like an identifier" vote must not override a specific header.
        Classify("ZIP", "75201", "10001").Category.ShouldBe(SensitiveDataType.Zip);
    }

    [Fact]
    public void Classify_MoneyHeaderWithNumbers_PassesThrough()
    {
        Classify("GrossPay", "1,234.56", "980.00").Category.ShouldBe(SensitiveDataType.Money);
    }

    [Fact]
    public void Classify_MoneyHeaderWithNonNumbers_IsMasked()
    {
        Classify("Rate", "Pat Smith", "Lee Kim").Category.ShouldBe(SensitiveDataType.FreeText);
    }

    [Fact]
    public void Classify_CodeHeaderWithLongText_IsMasked()
    {
        Classify("Status", "Spoke with the employee about their surgery next week").Category.ShouldBe(SensitiveDataType.FreeText);
    }

    [Fact]
    public void Classify_NumbersWithNoHeaderHint_AreIdentifiers()
    {
        Classify("Field_12", "1001", "1002").Category.ShouldBe(SensitiveDataType.Identifier);
    }

    /// <summary>The yes/no question: sensitive types say yes, pass-through types say no, unknown fails closed to yes.</summary>
    [Theory]
    [InlineData("SSN", "123-45-6789", true)]
    [InlineData("GrossPay", "1,234.56", false)]
    [InlineData("Status", "A", false)]
    [InlineData("Notes", "called on Tuesday", true)]
    public void ContainsSensitiveData_AnswersPerColumn(string header, string sample, bool expected)
    {
        _classifier.ContainsSensitiveData(header, [sample]).ShouldBe(expected);
    }

    /// <summary>One value on its own is flagged when its shape is distinctive (SSN, email, phone, date).</summary>
    [Theory]
    [InlineData("123-45-6789", true)]
    [InlineData("pat@example.com", true)]
    [InlineData("A", false)]
    [InlineData(null, false)]
    public void LooksLikeSensitiveValue_ByShape(string? value, bool expected)
    {
        _classifier.LooksLikeSensitiveValue(value).ShouldBe(expected);
    }

    /// <summary>Only Safe, Money and Code are not sensitive.</summary>
    [Fact]
    public void IsSensitive_OnlyPassThroughTypesAreNot()
    {
        Enum.GetValues<SensitiveDataType>().Where(t => !t.IsSensitive())
            .ShouldBe([SensitiveDataType.Safe, SensitiveDataType.Money, SensitiveDataType.Code], ignoreOrder: true);
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
