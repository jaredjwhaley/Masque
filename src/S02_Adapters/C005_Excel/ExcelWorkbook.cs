using System.Text;
using ExcelDataReader;
using Masque.Core.Tables;

namespace Masque.Excel;

/// <summary>
/// The workbook could not be read. The message names the file and the kind of failure but never cell
/// contents, and no inner exception is attached, so it is safe to show an AI assistant.
/// </summary>
/// <param name="message">What went wrong, free of data.</param>
public sealed class ExcelWorkbookException(string message) : Exception(message);

/// <summary>
/// Reads Excel workbooks (.xlsx, .xlsm, .xlsb and legacy .xls) as tables, one per non-empty worksheet.
/// The first non-blank row of a sheet is its header; the table's columns run from the header's first non-blank cell
/// to its last, and blank rows are skipped.
/// </summary>
public static class ExcelWorkbook
{
    /// <summary>File extensions this reader opens.</summary>
    public static readonly IReadOnlySet<string> Extensions =
        new HashSet<string>([".xlsx", ".xlsm", ".xlsb", ".xls"], StringComparer.OrdinalIgnoreCase);

    static ExcelWorkbook()
    {
        // Legacy .xls files use Windows code pages, which .NET only knows after this provider is registered.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Reads every non-empty worksheet.</summary>
    /// <param name="path">The workbook file.</param>
    /// <returns>One table per sheet that has a header row, named after the sheet, in workbook order.</returns>
    /// <exception cref="ExcelWorkbookException">The file isn't a readable workbook (damaged, encrypted or another format).</exception>
    public static IReadOnlyList<TableData> ReadTables(string path) => [.. ReadSheets(path).Select(sheet => sheet.Table)];

    /// <summary>Reads every non-empty worksheet, with where its header and rows sit on the sheet.</summary>
    /// <param name="path">The workbook file.</param>
    /// <returns>One entry per sheet that has a header row, in workbook order.</returns>
    /// <exception cref="ExcelWorkbookException">The file isn't a readable workbook.</exception>
    internal static IReadOnlyList<SheetTable> ReadSheets(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            var sheets = new List<SheetTable>();
            do
            {
                var sheet = ReadSheet(reader);
                if (sheet is not null)
                {
                    sheets.Add(sheet);
                }
            }
            while (reader.NextResult());

            return sheets;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or FileNotFoundException or DirectoryNotFoundException
            or UnauthorizedAccessException))
        {
            // The library's messages can quote cell text, so only the exception type is kept.
            throw new ExcelWorkbookException(
                $"Could not read {Path.GetFileName(path)} as an Excel workbook ({ex.GetType().Name}). " +
                "Check that it is a valid, unencrypted .xlsx, .xlsm, .xlsb or .xls file.");
        }
    }

    /// <summary>Reads the current sheet, or returns null when it has no non-blank row.</summary>
    private static SheetTable? ReadSheet(IExcelDataReader reader)
    {
        List<string>? headers = null;
        var first = 0;
        var headerRow = 0;
        var rows = new List<IReadOnlyList<string?>>();
        var rowNumbers = new List<int>();

        // The reader returns every row from the top of the sheet, blank ones included, so counting reads gives the
        // sheet row number (1-based, as Excel shows it).
        for (var rowNumber = 1; reader.Read(); rowNumber++)
        {
            var cells = Enumerable.Range(0, reader.FieldCount).Select(i => CellText.From(reader.GetValue(i))).ToList();
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (headers is null)
            {
                // The header runs from its first non-blank cell to its last; cells outside it have no column name.
                first = cells.FindIndex(c => !string.IsNullOrWhiteSpace(c));
                var width = cells.FindLastIndex(c => !string.IsNullOrWhiteSpace(c)) + 1 - first;
                headers = cells.Skip(first).Take(width).Select(c => c?.Trim() ?? string.Empty).ToList();
                headerRow = rowNumber;
                continue;
            }

            var row = cells.Skip(first).Take(headers.Count).ToList();
            if (row.Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                rows.Add(row);
                rowNumbers.Add(rowNumber);
            }
        }

        return headers is null
            ? null
            : new SheetTable(new TableData(reader.Name, headers, rows), new SheetLayout(reader.Name, headerRow, first + 1, rowNumbers));
    }
}

/// <summary>Where a table sits on its worksheet, so changes to it can be written to the right cells.</summary>
/// <param name="SheetName">The worksheet's name.</param>
/// <param name="HeaderRow">The header's sheet row number (1-based).</param>
/// <param name="FirstColumn">The sheet column number (1-based, A = 1) of the table's first column.</param>
/// <param name="DataRows">The sheet row number of each data row, in table order.</param>
internal sealed record SheetLayout(string SheetName, int HeaderRow, int FirstColumn, IReadOnlyList<int> DataRows);

/// <summary>A worksheet read as a table, with its layout.</summary>
/// <param name="Table">The table.</param>
/// <param name="Layout">Where it sits on the sheet.</param>
internal sealed record SheetTable(TableData Table, SheetLayout Layout);
