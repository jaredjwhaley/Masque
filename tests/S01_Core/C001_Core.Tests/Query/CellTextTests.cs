using System.Globalization;
using Masque.Core.Tables;

namespace Masque.Core.Tests.Query;

/// <summary>
/// Pins down how database and spreadsheet values become text before masking: culture-independent, dates in a
/// shape the date detector recognizes, and nulls kept as nulls.
/// </summary>
public sealed class CellTextTests
{
    /// <summary>Nulls and database nulls stay null, so "no value" never turns into a token.</summary>
    [Fact]
    public void From_Null_IsNull()
    {
        CellText.From(null).ShouldBeNull();
        CellText.From(DBNull.Value).ShouldBeNull();
    }

    /// <summary>Numbers use the invariant culture regardless of the machine's settings.</summary>
    [Fact]
    public void From_Numbers_UseInvariantCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            CellText.From(1234.5).ShouldBe("1234.5");
            CellText.From(1234.5m).ShouldBe("1234.5");
            CellText.From(42L).ShouldBe("42");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    /// <summary>Dates become ISO text (with the time only when there is one), which the date detector recognizes.</summary>
    [Fact]
    public void From_Dates_UseIsoFormat()
    {
        CellText.From(new DateTime(1980, 2, 3)).ShouldBe("1980-02-03");
        CellText.From(new DateTime(1980, 2, 3, 14, 5, 6)).ShouldBe("1980-02-03 14:05:06");
        CellText.From(new DateOnly(1980, 2, 3)).ShouldBe("1980-02-03");
    }

    /// <summary>Binary values are never shown; a placeholder stands in.</summary>
    [Fact]
    public void From_Binary_IsPlaceholder() => CellText.From(new byte[] { 1, 2, 3 }).ShouldBe("[binary]");
}
