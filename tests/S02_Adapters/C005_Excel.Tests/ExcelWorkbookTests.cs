using ClosedXML.Excel;

namespace Masque.Excel.Tests;

/// <summary>
/// Pins down reading a workbook as tables: one table per non-empty worksheet, the first non-blank row as the
/// header, values as culture-independent text, and errors that never quote cell contents.
/// </summary>
public sealed class ExcelWorkbookTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "masque-tests", $"{Guid.NewGuid():N}");

    public ExcelWorkbookTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Build(Action<XLWorkbook> fill, string name = "book.xlsx")
    {
        var path = Path.Combine(_folder, name);
        using var workbook = new XLWorkbook();
        fill(workbook);
        workbook.SaveAs(path);
        return path;
    }

    /// <summary>Each non-empty sheet becomes a table named after the sheet; empty sheets are skipped.</summary>
    [Fact]
    public void ReadTables_OneTablePerNonEmptySheet()
    {
        var path = Build(wb =>
        {
            var people = wb.AddWorksheet("People");
            people.Cell("A1").Value = "Ssn";
            people.Cell("A2").Value = "123-45-6789";
            wb.AddWorksheet("Empty");
            var visits = wb.AddWorksheet("Visits");
            visits.Cell("A1").Value = "VisitId";
            visits.Cell("A2").Value = 7;
        });

        var tables = ExcelWorkbook.ReadTables(path);

        tables.Select(t => t.Name).ShouldBe(["People", "Visits"]);
        tables[0].Headers.ShouldBe(["Ssn"]);
        tables[0].Rows.ShouldHaveSingleItem().ShouldBe(["123-45-6789"]);
    }

    /// <summary>Title rows above the data are not supported yet: the first non-blank row is the header, blank rows above it are skipped.</summary>
    [Fact]
    public void ReadTables_HeaderIsFirstNonBlankRow()
    {
        var path = Build(wb =>
        {
            var sheet = wb.AddWorksheet("S");
            sheet.Cell("A3").Value = "Name";
            sheet.Cell("A4").Value = "Zebulon";
        });

        var table = ExcelWorkbook.ReadTables(path).ShouldHaveSingleItem();

        table.Headers.ShouldBe(["Name"]);
        table.Rows.ShouldHaveSingleItem().ShouldBe(["Zebulon"]);
    }

    /// <summary>Numbers and dates become invariant text; dates in ISO form so the date detector sees them.</summary>
    [Fact]
    public void ReadTables_ConvertsTypedCells()
    {
        var path = Build(wb =>
        {
            var sheet = wb.AddWorksheet("S");
            sheet.Cell("A1").Value = "Pay";
            sheet.Cell("B1").Value = "Born";
            sheet.Cell("A2").Value = 1234.5;
            sheet.Cell("B2").Value = new DateTime(1980, 2, 3);
        });

        ExcelWorkbook.ReadTables(path)[0].Rows[0].ShouldBe(["1234.5", "1980-02-03"]);
    }

    /// <summary>Fully blank rows are dropped, and columns past the last header are ignored.</summary>
    [Fact]
    public void ReadTables_DropsBlankRowsAndHeaderlessColumns()
    {
        var path = Build(wb =>
        {
            var sheet = wb.AddWorksheet("S");
            sheet.Cell("A1").Value = "Id";
            sheet.Cell("A2").Value = 1;
            sheet.Cell("C2").Value = "stray";
            sheet.Cell("A4").Value = 2;
        });

        var table = ExcelWorkbook.ReadTables(path)[0];

        table.Headers.ShouldBe(["Id"]);
        table.Rows.Select(r => r[0]).ShouldBe(["1", "2"]);
    }

    /// <summary>An unreadable file fails with a safe error that names the file, not its contents.</summary>
    [Fact]
    public void ReadTables_CorruptFile_ThrowsSafeError()
    {
        var path = Path.Combine(_folder, "broken.xlsx");
        File.WriteAllText(path, "Zebulon was here, not a workbook");

        var error = Should.Throw<ExcelWorkbookException>(() => ExcelWorkbook.ReadTables(path));

        error.Message.ShouldContain("broken.xlsx");
        error.Message.ShouldNotContain("Zebulon");
        error.InnerException.ShouldBeNull();
    }
}
