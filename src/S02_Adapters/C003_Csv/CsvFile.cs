using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Masque.Core.Tables;

namespace Masque.Csv;

/// <summary>
/// A CSV file read as one table, which can save changes back to the file. Saving keeps the delimiter, byte order
/// mark, line endings and the exact text of every cell that didn't change; it rewrites quoting where a field needs it
/// and drops blank lines, as any CSV writer would.
/// </summary>
public sealed class CsvFile : ITableFile
{
    private readonly TableData _table;
    private readonly string _delimiter;
    private readonly bool _hasByteOrderMark;
    private readonly string _newLine;
    private readonly FileStamp _stamp;

    private CsvFile(string path, TableData table, string delimiter, bool hasByteOrderMark, string newLine, FileStamp stamp)
    {
        Path = path;
        _table = table;
        _delimiter = delimiter;
        _hasByteOrderMark = hasByteOrderMark;
        _newLine = newLine;
        _stamp = stamp;
    }

    /// <inheritdoc />
    public string Path { get; }

    /// <inheritdoc />
    public IReadOnlyList<TableData> Tables => [_table];

    /// <summary>Reads a CSV file, noting how it is written so a save can match it.</summary>
    /// <param name="path">The CSV file.</param>
    /// <returns>The file, read.</returns>
    /// <exception cref="CsvTableException">The file is empty or badly quoted.</exception>
    public static CsvFile Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Stamped before reading, so a change made while reading is caught at save time.
        var stamp = FileStamp.Of(path);
        var table = CsvTable.ReadTable(path, out var delimiter);
        return new CsvFile(path, table, delimiter, CsvTable.HasUtf8ByteOrderMark(path), DetectNewLine(path), stamp);
    }

    /// <inheritdoc />
    public string Save(IReadOnlyList<TableChanges> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count != 1)
        {
            throw new ArgumentException("A CSV file holds one table, so it takes one set of changes.", nameof(changes));
        }

        var rows = Apply(changes[0]);
        return FileReplacer.Replace(Path, _stamp, stream => Write(stream, rows));
    }

    /// <summary>The file's rows after the changes: updated cells, deleted rows dropped, inserted rows at the end.</summary>
    private List<string?[]> Apply(TableChanges change)
    {
        var rows = _table.Rows.Select(row => row.ToArray()).ToList();

        foreach (var update in change.Updates)
        {
            var row = rows[update.Row];
            if (update.Column >= row.Length)
            {
                // A short row gets the missing cells before the changed one.
                Array.Resize(ref row, update.Column + 1);
                rows[update.Row] = row;
            }

            row[update.Column] = Text(update.Value);
        }

        var deleted = change.DeletedRows.ToHashSet();
        var kept = rows.Where((_, i) => !deleted.Contains(i)).ToList();
        kept.AddRange(change.InsertedRows.Select(values => values.Select(Text).ToArray()));
        return kept;
    }

    private void Write(Stream stream, IReadOnlyList<string?[]> rows)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(_hasByteOrderMark), leaveOpen: true);
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = _delimiter,
            NewLine = _newLine,
        });

        foreach (var row in rows.Prepend([.. _table.Headers]))
        {
            foreach (var field in row)
            {
                csv.WriteField(field ?? string.Empty);
            }

            csv.NextRecord();
        }
    }

    /// <summary>A changed value as CSV text: numbers in invariant form, null as a blank.</summary>
    private static string Text(object? value) => CellText.From(value) ?? string.Empty;

    /// <summary>The line ending the file uses, judged from its first line; files without one get "\n".</summary>
    private static string DetectNewLine(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        int current;
        var previous = -1;
        while ((current = reader.Read()) != -1)
        {
            if (current == '\n')
            {
                return previous == '\r' ? "\r\n" : "\n";
            }

            previous = current;
        }

        return "\n";
    }
}
