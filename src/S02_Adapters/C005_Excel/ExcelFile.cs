using System.Globalization;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using Masque.Core.Tables;

namespace Masque.Excel;

/// <summary>
/// An Excel workbook read as tables (one per non-empty sheet), which can save changes back to the workbook. Only the
/// changed cells are set, so formatting, formulas and other sheets are kept. Saving is limited to .xlsx workbooks
/// without charts or pivot tables, which the Excel library can't be trusted to keep; anything it can't save safely is
/// refused before the file is touched.
/// </summary>
public sealed class ExcelFile : ITableFile
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss"];

    private readonly IReadOnlyList<SheetTable> _sheets;
    private readonly FileStamp _stamp;

    private ExcelFile(string path, IReadOnlyList<SheetTable> sheets, FileStamp stamp)
    {
        Path = path;
        _sheets = sheets;
        _stamp = stamp;
    }

    /// <inheritdoc />
    public string Path { get; }

    /// <inheritdoc />
    public IReadOnlyList<TableData> Tables => [.. _sheets.Select(sheet => sheet.Table)];

    /// <summary>Reads a workbook, noting where each table sits on its sheet so a save can find the right cells.</summary>
    /// <param name="path">The workbook file (.xlsx, .xlsm, .xlsb or .xls; only .xlsx can be saved).</param>
    /// <returns>The workbook, read.</returns>
    /// <exception cref="ExcelWorkbookException">The file isn't a readable workbook.</exception>
    public static ExcelFile Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Stamped before reading, so a change made while reading is caught at save time.
        var stamp = FileStamp.Of(path);
        return new ExcelFile(path, ExcelWorkbook.ReadSheets(path), stamp);
    }

    /// <inheritdoc />
    public string Save(IReadOnlyList<TableChanges> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count != _sheets.Count)
        {
            throw new ArgumentException("Give one set of changes per table, in table order.", nameof(changes));
        }

        var name = System.IO.Path.GetFileName(Path);
        var extension = System.IO.Path.GetExtension(Path);
        if (!string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new TableFileException(
                $"Masque saves changes only to .xlsx workbooks, and {name} is {extension}. Save a copy as .xlsx in Excel and use that, or make this change in Excel.");
        }

        var content = File.ReadAllBytes(Path);
        RefuseWhatTheLibraryMightDrop(name, content);

        using var workbook = OpenWorkbook(name, content);
        var sheets = _sheets.Select(sheet => workbook.Worksheet(sheet.Layout.SheetName)).ToList();

        // Every check runs before any cell is set, so a refusal never leaves a half-changed workbook behind.
        for (var i = 0; i < changes.Count; i++)
        {
            RefuseFormulaUpdates(name, sheets[i], _sheets[i].Layout, changes[i]);
        }

        for (var i = 0; i < changes.Count; i++)
        {
            Apply(sheets[i], _sheets[i], changes[i]);
        }

        return FileReplacer.Replace(Path, _stamp, stream => workbook.SaveAs(stream));
    }

    /// <summary>Sets updated cells, adds inserted rows below the table, then removes deleted rows from the bottom up.</summary>
    private static void Apply(IXLWorksheet sheet, SheetTable table, TableChanges change)
    {
        if (change.IsEmpty)
        {
            return;
        }

        var layout = table.Layout;
        var lastColumn = layout.FirstColumn + table.Table.Headers.Count - 1;

        foreach (var update in change.Updates)
        {
            SetValue(sheet.Cell(layout.DataRows[update.Row], layout.FirstColumn + update.Column), update.Value);
        }

        if (change.InsertedRows.Count > 0)
        {
            // New rows go right below the table, shifting only the table's columns, so content beside it stays put.
            var last = layout.DataRows.Count > 0 ? layout.DataRows[^1] : layout.HeaderRow;
            sheet.Range(last, layout.FirstColumn, last, lastColumn).InsertRowsBelow(change.InsertedRows.Count);
            for (var i = 0; i < change.InsertedRows.Count; i++)
            {
                var values = change.InsertedRows[i];
                for (var column = 0; column < values.Count; column++)
                {
                    // The cell above shows what kind of value the column holds (a date, say).
                    var template = sheet.Cell(last, layout.FirstColumn + column);
                    SetValue(sheet.Cell(last + 1 + i, layout.FirstColumn + column), values[column], template);
                }
            }
        }

        foreach (var row in change.DeletedRows.Select(i => layout.DataRows[i]).OrderDescending())
        {
            sheet.Range(row, layout.FirstColumn, row, lastColumn).Delete(XLShiftDeletedCells.ShiftCellsUp);
        }
    }

    /// <summary>
    /// Sets a cell from a changed value: numbers as numbers, null as an empty cell, and text as text, except that an
    /// ISO date written where the column holds dates is stored as a date, since dates are read as ISO text.
    /// </summary>
    private static void SetValue(IXLCell cell, object? value, IXLCell? template = null)
    {
        var kind = (template ?? cell).DataType;
        switch (value)
        {
            case null:
                cell.Clear(XLClearOptions.Contents);
                break;
            case long number:
                cell.Value = number;
                break;
            case double number:
                cell.Value = number;
                break;
            case string text when kind == XLDataType.DateTime
                && DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                cell.Value = date;
                break;
            case string text:
                cell.Value = text;
                break;
            default:
                throw new ArgumentException($"Unexpected value type {value.GetType().Name}.", nameof(value));
        }
    }

    private static void RefuseFormulaUpdates(string name, IXLWorksheet sheet, SheetLayout layout, TableChanges change)
    {
        foreach (var update in change.Updates)
        {
            var cell = sheet.Cell(layout.DataRows[update.Row], layout.FirstColumn + update.Column);
            if (cell.HasFormula)
            {
                throw new TableFileException(
                    $"Cell {cell.Address.ToStringRelative()} on sheet {layout.SheetName} in {name} holds a formula, which Masque won't overwrite with a value. Nothing was saved; change the formula in Excel.");
            }
        }
    }

    /// <summary>Refuses workbooks with parts the Excel library doesn't fully support and could drop on save.</summary>
    private static void RefuseWhatTheLibraryMightDrop(string name, byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);
        var workbook = document.WorkbookPart;
        if (workbook is null)
        {
            return;
        }

        var hasCharts = workbook.ChartsheetParts.Any()
            || workbook.WorksheetParts.Any(sheet => sheet.DrawingsPart?.ChartParts.Any() == true);
        var hasPivotTables = workbook.PivotTableCacheDefinitionParts.Any()
            || workbook.WorksheetParts.Any(sheet => sheet.PivotTableParts.Any());

        if (hasCharts || hasPivotTables)
        {
            var what = (hasCharts, hasPivotTables) switch
            {
                (true, true) => "charts and pivot tables",
                (true, false) => "charts",
                _ => "pivot tables",
            };

            throw new TableFileException(
                $"{name} has {what}, which Masque's Excel library might not keep when saving. Nothing was saved; make this change in Excel, or keep the data in a workbook without them.");
        }
    }

    private static XLWorkbook OpenWorkbook(string name, byte[] content)
    {
        try
        {
            return new XLWorkbook(new MemoryStream(content, writable: false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The library's messages can quote cell text, so only the exception type is kept.
            throw new TableFileException($"Could not open {name} to save changes ({ex.GetType().Name}). Nothing was saved.");
        }
    }
}
