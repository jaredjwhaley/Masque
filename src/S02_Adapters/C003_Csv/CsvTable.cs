using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Masque.Core;
using Masque.Core.Tables;

namespace Masque.Csv;

/// <summary>
/// The CSV could not be read. The message names the row but never quotes data, so it is safe to print.
/// No inner exception is attached, because the CSV library's messages include the raw row.
/// </summary>
/// <param name="message">What went wrong and where.</param>
public sealed class CsvTableException(string message) : Exception(message);

/// <summary>The first rows of a CSV file, gathered for column detection.</summary>
/// <param name="Headers">The header row as written.</param>
/// <param name="ColumnSamples">For each header, its values from the sampled rows (null where a row was short).</param>
/// <param name="Delimiter">The detected field separator.</param>
/// <param name="HasByteOrderMark">Whether the file starts with a UTF-8 byte order mark.</param>
public sealed record CsvSample(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string?>> ColumnSamples,
    string Delimiter,
    bool HasByteOrderMark);

/// <summary>
/// Reads and writes CSV files for Masque. The output keeps the input's delimiter, header row and byte order
/// mark, so tools that read the original can read the masked copy.
/// </summary>
public static class CsvTable
{
    /// <summary>Rows read for detection by default.</summary>
    public const int DefaultSampleRows = 500;

    /// <summary>Reads the header row and up to <paramref name="maxRows"/> data rows.</summary>
    /// <param name="path">The CSV file.</param>
    /// <param name="maxRows">Most data rows to sample.</param>
    /// <returns>Headers, per-column samples and format details.</returns>
    /// <exception cref="CsvTableException">The file is empty or badly quoted.</exception>
    public static CsvSample ReadSample(string path, int maxRows = DefaultSampleRows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var hasBom = HasUtf8ByteOrderMark(path);
        using var parser = CreateParser(path);
        var headers = ReadHeaders(parser);

        var samples = headers.Select(_ => new List<string?>()).ToList();
        for (var rows = 0; rows < maxRows && Read(parser); rows++)
        {
            var record = parser.Record!;
            for (var i = 0; i < headers.Count; i++)
            {
                samples[i].Add(i < record.Length ? record[i] : null);
            }
        }

        return new CsvSample(headers, samples, parser.Delimiter, hasBom);
    }

    /// <summary>Reads the whole file as a table named after the file, for loading into a query database.</summary>
    /// <param name="path">The CSV file.</param>
    /// <returns>The header row and every data row, as written.</returns>
    /// <exception cref="CsvTableException">The file is empty or badly quoted.</exception>
    public static TableData ReadTable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var parser = CreateParser(path);
        var headers = ReadHeaders(parser);

        var rows = new List<IReadOnlyList<string?>>();
        while (Read(parser))
        {
            rows.Add([.. parser.Record!]);
        }

        return new TableData(Path.GetFileNameWithoutExtension(path), headers, rows);
    }

    /// <summary>
    /// Writes a copy of the file with every cell tokenized by its column's category. Cells beyond the header's
    /// width are masked as free text. The output appears only once it is complete.
    /// </summary>
    /// <param name="inputPath">The CSV file to read.</param>
    /// <param name="outputPath">Where to write the masked copy. Must differ from the input.</param>
    /// <param name="categories">One category per column, in column order.</param>
    /// <param name="tokenizer">Turns values into tokens.</param>
    /// <returns>The number of data rows written.</returns>
    /// <exception cref="CsvTableException">The file is empty or badly quoted.</exception>
    public static int Mask(string inputPath, string outputPath, IReadOnlyList<SensitiveDataType> categories, Tokenizer tokenizer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(tokenizer);

        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The output must be a different file from the input.", nameof(outputPath));
        }

        var partialPath = outputPath + ".partial";
        var rows = 0;
        try
        {
            using (var parser = CreateParser(inputPath))
            {
                // The delimiter is detected on the first read, so read the headers before creating the writer.
                var headers = ReadHeaders(parser);

                using var writer = new StreamWriter(partialPath, append: false, new UTF8Encoding(HasUtf8ByteOrderMark(inputPath)));
                using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = parser.Delimiter });

                foreach (var header in headers)
                {
                    csv.WriteField(header);
                }

                csv.NextRecord();

                while (Read(parser))
                {
                    var record = parser.Record!;
                    for (var i = 0; i < record.Length; i++)
                    {
                        var category = i < categories.Count ? categories[i] : SensitiveDataType.FreeText;
                        csv.WriteField(tokenizer.Tokenize(category, record[i]));
                    }

                    csv.NextRecord();
                    rows++;
                }
            }

            File.Move(partialPath, outputPath, overwrite: true);
            return rows;
        }
        catch
        {
            File.Delete(partialPath);
            throw;
        }
    }

    private static CsvParser CreateParser(string path)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,

            // Fail on stray quotes instead of guessing; the message names the row, never the data.
            BadDataFound = args => throw new CsvTableException(
                $"Bad quoting on row {args.Context.Parser?.Row}. Fix the quotes in that row and try again."),
        };

        return new CsvParser(new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true), config);
    }

    private static List<string> ReadHeaders(CsvParser parser)
    {
        if (!Read(parser))
        {
            throw new CsvTableException("The file is empty; a header row is required.");
        }

        return [.. parser.Record!];
    }

    /// <summary>Reads the next row, turning the library's errors (which quote the raw row) into safe ones.</summary>
    private static bool Read(CsvParser parser)
    {
        try
        {
            return parser.Read();
        }
        catch (CsvHelperException)
        {
            throw new CsvTableException($"The CSV could not be read at row {parser.Row}. Check that row's quoting.");
        }
    }

    private static bool HasUtf8ByteOrderMark(string path)
    {
        Span<byte> start = stackalloc byte[3];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(start, 3, throwOnEndOfStream: false) == 3 && start.SequenceEqual(Encoding.UTF8.Preamble);
    }
}
