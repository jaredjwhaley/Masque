using ClosedXML.Excel;
using Masque.Core.Tables;

namespace Masque.Excel.Tests;

/// <summary>
/// Pins down saving changes back to an .xlsx workbook, or to a new copy of it: only the changed cells are set
/// (numbers as numbers, ISO dates as dates), the rest of the workbook (formatting, formulas, other sheets) is kept,
/// rows are found where they really are on the sheet, and anything Masque can't save safely is refused before any
/// file is written.
/// </summary>
public sealed class ExcelFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "masque-tests", $"{Guid.NewGuid():N}");

    public ExcelFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static TableChanges Changes(
        IReadOnlyList<CellChange>? updates = null,
        IReadOnlyList<int>? deleted = null,
        IReadOnlyList<IReadOnlyList<object?>>? inserted = null) =>
        new(updates ?? [], deleted ?? [], inserted ?? []);

    /// <summary>
    /// A "People" sheet whose table starts at B3 with a blank row inside the data, a bold header, a total formula
    /// beside it, and a second "Notes" sheet.
    /// </summary>
    private string Build(string name = "book.xlsx")
    {
        var path = Path.Combine(_folder, name);
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("People");
        sheet.Cell("B3").Value = "Name";
        sheet.Cell("C3").Value = "Zip";
        sheet.Cell("D3").Value = "Pay";
        sheet.Range("B3:D3").Style.Font.Bold = true;
        sheet.Cell("B4").Value = "Ana";
        sheet.Cell("C4").Value = "02134";
        sheet.Cell("D4").Value = 10;
        sheet.Cell("B6").Value = "Lee";
        sheet.Cell("C6").Value = "60601";
        sheet.Cell("D6").Value = 20;
        sheet.Cell("F1").FormulaA1 = "SUM(D4:D6)";
        workbook.AddWorksheet("Notes").Cell("A1").Value = "Note";
        workbook.Worksheet("Notes").Cell("A2").Value = "keep me";
        workbook.SaveAs(path);
        return path;
    }

    /// <summary>
    /// A payroll-shaped "Export" sheet (synthetic values): a bold header, PayDate as real dates shown as mm/dd/yyyy in
    /// a widened column, the second row's PayDate left blank when <paramref name="blankDate"/> is set, and a second
    /// "Totals" sheet.
    /// </summary>
    private string BuildPayroll(bool blankDate = false)
    {
        var path = Path.Combine(_folder, "export.xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Export");
        string[] headers = ["EmployeeName", "Ssn", "PayDate", "Hours", "GrossPay"];
        for (var i = 0; i < headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
        }

        sheet.Row(1).Style.Font.Bold = true;
        (string Name, string Ssn, int Hours, double Pay)[] rows =
        [
            ("Ana Zed", "900-11-1111", 40, 1000.5),
            ("Bo Yen", "900-22-2222", 32, 800.25),
            ("Cy Xu", "900-33-3333", 38, 950),
        ];
        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Cell(r + 2, 1).Value = rows[r].Name;
            sheet.Cell(r + 2, 2).Value = rows[r].Ssn;
            sheet.Cell(r + 2, 3).Value = new DateTime(2026, 11, 20);
            sheet.Cell(r + 2, 4).Value = rows[r].Hours;
            sheet.Cell(r + 2, 5).Value = rows[r].Pay;
        }

        sheet.Range("C2:C4").Style.NumberFormat.Format = "mm/dd/yyyy";
        sheet.Column(3).Width = 14;
        if (blankDate)
        {
            sheet.Cell("C3").Clear(XLClearOptions.Contents);
        }

        workbook.AddWorksheet("Totals").Cell("A1").Value = "Run total";
        workbook.SaveAs(path);
        return path;
    }

    /// <summary>One update per data row setting PayDate (column 2) to the same ISO date, as SQL text arrives.</summary>
    private static TableChanges SetEveryPayDate(int rows, string isoDate) =>
        Changes(updates: [.. Enumerable.Range(0, rows).Select(row => new CellChange(row, 2, isoDate))]);

    /// <summary>
    /// The weekly payroll case: every PayDate set to one date and saved as a copy. The copy holds that date as a real
    /// date with the column's mm/dd/yyyy format; every other cell, the header style, the column width and the second
    /// sheet are kept; the source workbook is left byte for byte as it was, with no backup made.
    /// </summary>
    [Fact]
    public void SaveCopy_PayDateForEveryRow_KeepsDatesFormatAndTheRest()
    {
        var path = BuildPayroll();
        var before = File.ReadAllBytes(path);
        var file = ExcelFile.Open(path);

        var copy = file.SaveCopy([SetEveryPayDate(3, "2026-11-27"), Changes()], "export-2026-11-27.xlsx");

        copy.ShouldBe(Path.Combine(_folder, "export-2026-11-27.xlsx"));
        File.ReadAllBytes(path).ShouldBe(before);
        Directory.GetFiles(_folder).Length.ShouldBe(2);
        using var workbook = new XLWorkbook(copy);
        var sheet = workbook.Worksheet("Export");
        for (var row = 2; row <= 4; row++)
        {
            sheet.Cell(row, 3).DataType.ShouldBe(XLDataType.DateTime);
            sheet.Cell(row, 3).GetDateTime().ShouldBe(new DateTime(2026, 11, 27));
            sheet.Cell(row, 3).Style.NumberFormat.Format.ShouldBe("mm/dd/yyyy");
        }

        sheet.Cell("A2").GetText().ShouldBe("Ana Zed");
        sheet.Cell("B3").GetText().ShouldBe("900-22-2222");
        sheet.Cell("D4").GetDouble().ShouldBe(38);
        sheet.Cell("E2").GetDouble().ShouldBe(1000.5);
        sheet.Cell("A1").Style.Font.Bold.ShouldBeTrue();
        sheet.Column(3).Width.ShouldBe(14);
        workbook.Worksheet("Totals").Cell("A1").GetText().ShouldBe("Run total");
    }

    /// <summary>
    /// An ISO date written into a blank cell of a date column is stored as a date, taking its kind from the column's
    /// other cells rather than from the blank cell itself.
    /// </summary>
    [Fact]
    public void Save_IsoDateIntoBlankCellOfDateColumn_StoresADate()
    {
        var path = BuildPayroll(blankDate: true);
        var file = ExcelFile.Open(path);

        file.Save([SetEveryPayDate(3, "2026-11-27"), Changes()]);

        using var workbook = new XLWorkbook(path);
        var cell = workbook.Worksheet("Export").Cell("C3");
        cell.DataType.ShouldBe(XLDataType.DateTime);
        cell.GetDateTime().ShouldBe(new DateTime(2026, 11, 27));
    }

    /// <summary>The checks that guard a save also guard a copy: a formula cell is refused and no copy is written.</summary>
    [Fact]
    public void SaveCopy_UpdateOfAFormulaCell_IsRefusedAndWritesNothing()
    {
        var path = Build();
        using (var workbook = new XLWorkbook(path))
        {
            workbook.Worksheet("People").Cell("D4").FormulaA1 = "5*2";
            workbook.Save();
        }

        var file = ExcelFile.Open(path);

        Should.Throw<TableFileException>(() => file.SaveCopy([Changes(updates: [new CellChange(0, 2, 7L)]), Changes()], "copy.xlsx"))
            .Message.ShouldContain("formula");
        File.Exists(Path.Combine(_folder, "copy.xlsx")).ShouldBeFalse();
    }

    /// <summary>The workbook reads as the same tables <see cref="ExcelWorkbook.ReadTables"/> gives.</summary>
    [Fact]
    public void Open_ReadsTheSameTablesAsTheReader()
    {
        var path = Build();

        var file = ExcelFile.Open(path);

        file.Path.ShouldBe(path);
        var expected = ExcelWorkbook.ReadTables(path);
        file.Tables.Select(t => t.Name).ShouldBe(expected.Select(t => t.Name));
        file.Tables[0].Headers.ShouldBe(["Name", "Zip", "Pay"]);
        file.Tables[0].Rows.ShouldBe([["Ana", "02134", "10"], ["Lee", "60601", "20"]]);
    }

    /// <summary>
    /// Updates land on the right sheet cells even when the table starts below and right of A1 and has a blank row;
    /// numbers are stored as numbers, NULL clears the value, and formatting, formulas and other sheets are kept.
    /// </summary>
    [Fact]
    public void Save_Update_SetsOnlyChangedCells()
    {
        var path = Build();
        var file = ExcelFile.Open(path);

        file.Save([Changes(updates: [new CellChange(1, 2, 25L), new CellChange(0, 0, null), new CellChange(1, 1, "00501")]), Changes()]);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("People");
        sheet.Cell("D6").Value.GetNumber().ShouldBe(25);
        sheet.Cell("C6").Value.GetText().ShouldBe("00501");
        sheet.Cell("B4").IsEmpty().ShouldBeTrue();
        sheet.Cell("C4").Value.GetText().ShouldBe("02134");
        sheet.Cell("B3").Style.Font.Bold.ShouldBeTrue();
        sheet.Cell("F1").FormulaA1.ShouldBe("SUM(D4:D6)");
        workbook.Worksheet("Notes").Cell("A2").Value.GetText().ShouldBe("keep me");
    }

    /// <summary>Inserted rows go right below the table's last row; deleted rows are removed from the sheet.</summary>
    [Fact]
    public void Save_DeleteAndInsert_ChangesTheRightRows()
    {
        var path = Build();
        var file = ExcelFile.Open(path);

        file.Save([Changes(deleted: [0], inserted: [["Cy", "73301", 30d]]), Changes()]);

        var table = ExcelWorkbook.ReadTables(path)[0];
        table.Rows.ShouldBe([["Lee", "60601", "20"], ["Cy", "73301", "30"]]);
    }

    /// <summary>The previous version is kept beside the workbook as a dated backup.</summary>
    [Fact]
    public void Save_KeepsTheOriginalAsABackup()
    {
        var path = Build();
        var before = File.ReadAllBytes(path);
        var file = ExcelFile.Open(path);

        var backup = file.Save([Changes(deleted: [1]), Changes()]);

        backup.ShouldEndWith(".masque-backup.xlsx");
        File.ReadAllBytes(backup).ShouldBe(before);
    }

    /// <summary>A cell that holds a formula is never overwritten with a value; nothing is saved.</summary>
    [Fact]
    public void Save_UpdateOfAFormulaCell_IsRefused()
    {
        var path = Path.Combine(_folder, "formula.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("S");
            sheet.Cell("A1").Value = "Pay";
            sheet.Cell("B1").Value = "Double";
            sheet.Cell("A2").Value = 5;
            sheet.Cell("B2").FormulaA1 = "A2*2";
            workbook.SaveAs(path);
        }

        var before = File.ReadAllBytes(path);
        var file = ExcelFile.Open(path);

        Should.Throw<TableFileException>(() => file.Save([Changes(updates: [new CellChange(0, 0, 6L), new CellChange(0, 1, 3L)])]))
            .Message.ShouldContain("formula");

        File.ReadAllBytes(path).ShouldBe(before);
    }

    /// <summary>Only .xlsx workbooks are saved; legacy formats are refused with a pointer to the fix.</summary>
    [Theory]
    [InlineData(".xls")]
    [InlineData(".xlsb")]
    [InlineData(".xlsm")]
    public void Save_OtherFormats_AreRefused(string extension)
    {
        var path = Path.ChangeExtension(Build(), extension);
        File.Move(Path.Combine(_folder, "book.xlsx"), path);
        var file = ExcelFile.Open(path);

        Should.Throw<TableFileException>(() => file.Save([Changes(deleted: [0]), Changes()])).Message.ShouldContain(".xlsx");
    }

    /// <summary>A workbook with a pivot table is refused, because the Excel library might not keep it.</summary>
    [Fact]
    public void Save_WorkbookWithAPivotTable_IsRefused()
    {
        var path = Path.Combine(_folder, "pivot.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Data");
            sheet.Cell("A1").Value = "Name";
            sheet.Cell("B1").Value = "Pay";
            sheet.Cell("A2").Value = "Ana";
            sheet.Cell("B2").Value = 10;
            var pivot = workbook.AddWorksheet("Pivot").PivotTables.Add("P", workbook.Worksheet("Pivot").Cell("A1"), sheet.Range("A1:B2"));
            pivot.RowLabels.Add("Name");
            pivot.Values.Add("Pay");
            workbook.SaveAs(path);
        }

        var before = File.ReadAllBytes(path);
        var file = ExcelFile.Open(path);
        var changes = file.Tables.Select(_ => Changes()).ToList();
        changes[0] = Changes(deleted: [0]);

        Should.Throw<TableFileException>(() => file.Save(changes)).Message.ShouldContain("pivot");

        File.ReadAllBytes(path).ShouldBe(before);
    }

    /// <summary>A workbook changed on disk after it was read is left alone.</summary>
    [Fact]
    public void Save_FileChangedOnDisk_IsRefused()
    {
        var path = Build();
        var file = ExcelFile.Open(path);
        using (var workbook = new XLWorkbook(path))
        {
            workbook.Worksheet("Notes").Cell("A3").Value = "someone else's edit";
            workbook.Save();
        }

        Should.Throw<TableFileException>(() => file.Save([Changes(deleted: [0]), Changes()])).Message.ShouldContain("changed");
    }
}
