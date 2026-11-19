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
/// The first non-blank row of a sheet is its header; columns past the last header are ignored.
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
    public static IReadOnlyList<TableData> ReadTables(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            var tables = new List<TableData>();
            do
            {
                var table = ReadSheet(reader);
                if (table is not null)
                {
                    tables.Add(table);
                }
            }
            while (reader.NextResult());

            return tables;
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
    private static TableData? ReadSheet(IExcelDataReader reader)
    {
        List<string>? headers = null;
        var rows = new List<IReadOnlyList<string?>>();

        while (reader.Read())
        {
            var cells = Enumerable.Range(0, reader.FieldCount).Select(i => CellText.From(reader.GetValue(i))).ToList();
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (headers is null)
            {
                // The header ends at its last non-blank cell; anything to the right has no column name.
                var width = cells.FindLastIndex(c => !string.IsNullOrWhiteSpace(c)) + 1;
                headers = cells.Take(width).Select(c => c?.Trim() ?? string.Empty).ToList();
                continue;
            }

            var row = cells.Take(headers.Count).ToList();
            if (row.Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                rows.Add(row);
            }
        }

        return headers is null ? null : new TableData(reader.Name, headers, rows);
    }
}
