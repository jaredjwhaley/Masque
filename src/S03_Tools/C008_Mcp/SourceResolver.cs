using System.Data.Common;
using Masque.Core.Tables;
using Masque.Csv;
using Masque.Excel;
using Masque.Sqlite;
using Masque.SqlServer;
using Microsoft.Data.SqlClient;

namespace Masque.Mcp;

/// <summary>The source couldn't be found or opened. The message names the source, never data.</summary>
/// <param name="message">What went wrong.</param>
public sealed class SourceException(string message) : Exception(message);

/// <summary>An open data source: a raw connection for Masque to wrap, and the name to show for it.</summary>
/// <param name="Name">The source's display name (a file, folder or named source).</param>
/// <param name="Connection">The open raw connection; disposed with this object.</param>
/// <param name="IsFileCopy">
/// Whether the tables are an in-memory copy of CSV or Excel files, which can be read but not written.
/// </param>
public sealed record OpenedSource(string Name, DbConnection Connection, bool IsFileCopy) : IDisposable
{
    /// <inheritdoc />
    public void Dispose() => Connection.Dispose();
}

/// <summary>Turns the text an assistant passes as "source" into something Masque can query.</summary>
public interface ISourceResolver
{
    /// <summary>Opens a source.</summary>
    /// <param name="source">A name from sources.json, a CSV/Excel/SQLite file path, or a folder path.</param>
    /// <returns>An open source; the caller disposes it.</returns>
    /// <exception cref="SourceException">Nothing matches, or the file type isn't supported.</exception>
    OpenedSource Open(string source);
}

/// <summary>
/// Resolves sources in this order: a name in sources.json, then a folder (every CSV and Excel file in it becomes a
/// table, so files can be joined), then a single file chosen by extension. CSV and Excel data is loaded into a
/// private in-memory SQLite database for the length of one call.
/// </summary>
/// <param name="catalog">Named sources.</param>
public sealed class SourceResolver(SourceCatalog catalog) : ISourceResolver
{
    /// <summary>File extensions read as delimited text.</summary>
    public static readonly IReadOnlySet<string> CsvExtensions =
        new HashSet<string>([".csv", ".tsv"], StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public OpenedSource Open(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new SourceException("No source given. " + Help());
        }

        source = source.Trim();

        if (catalog.TryGet(source, out var entry))
        {
            return OpenNamed(source, entry);
        }

        if (Directory.Exists(source))
        {
            return OpenFolder(source);
        }

        if (File.Exists(source))
        {
            return OpenFile(source);
        }

        throw new SourceException($"No source matches {source}. " + Help());
    }

    private OpenedSource OpenNamed(string name, SourceEntry entry) => entry.Type switch
    {
        SourceType.SqlServer when !string.IsNullOrWhiteSpace(entry.ConnectionString) => OpenSqlServer(name, entry.ConnectionString),
        SourceType.Sqlite when !string.IsNullOrWhiteSpace(entry.Path) => OpenSqlite(entry.Path),
        SourceType.File when !string.IsNullOrWhiteSpace(entry.Path) => Directory.Exists(entry.Path) ? OpenFolder(entry.Path) : OpenFile(entry.Path),
        _ => throw new SourceException(
            $"Source {name} in {catalog.FilePath} is incomplete: sqlServer needs a connectionString; sqlite and file need a path."),
    };

    private OpenedSource OpenFile(string path)
    {
        var extension = Path.GetExtension(path);

        if (SqliteSources.Extensions.Contains(extension))
        {
            return OpenSqlite(path);
        }

        if (CsvExtensions.Contains(extension))
        {
            return Load(Path.GetFileName(path), [CsvTable.ReadTable(path)]);
        }

        if (ExcelWorkbook.Extensions.Contains(extension))
        {
            // A single workbook's tables are its sheets, named as in Excel.
            return Load(Path.GetFileName(path), ExcelWorkbook.ReadTables(path));
        }

        throw new SourceException($"Masque can't read {extension} files. " + Help());
    }

    private static OpenedSource OpenFolder(string folder)
    {
        var tables = new List<TableData>();
        foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var extension = Path.GetExtension(file);
            if (CsvExtensions.Contains(extension))
            {
                tables.Add(CsvTable.ReadTable(file));
            }
            else if (ExcelWorkbook.Extensions.Contains(extension))
            {
                tables.AddRange(WorkbookTablesInFolder(file));
            }
        }

        if (tables.Count == 0)
        {
            throw new SourceException($"The folder {folder} has no CSV or Excel files.");
        }

        return Load(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), tables);
    }

    /// <summary>In a folder, a one-sheet workbook is named after its file; sheets of a larger workbook are "file_sheet".</summary>
    private static IEnumerable<TableData> WorkbookTablesInFolder(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var sheets = ExcelWorkbook.ReadTables(file);
        return sheets.Count == 1
            ? [sheets[0] with { Name = stem }]
            : sheets.Select(sheet => sheet with { Name = $"{stem}_{sheet.Name}" });
    }

    /// <summary>Checks every table has a real header row (before names are made SQL-safe), then loads them.</summary>
    private static OpenedSource Load(string name, IReadOnlyList<TableData> tables)
    {
        foreach (var table in tables)
        {
            HeaderRow.EnsureLooksLikeHeaders(table.Name, table.Headers);
        }

        return new OpenedSource(name, SqliteSources.LoadTables(tables), IsFileCopy: true);
    }

    private static OpenedSource OpenSqlite(string path) => new(Path.GetFileName(path), SqliteSources.OpenDatabase(path), IsFileCopy: false);

    /// <summary>Opens a SQL Server source; connection failures name the source, never the connection string.</summary>
    private static OpenedSource OpenSqlServer(string name, string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            connection.Open();
            return new OpenedSource(name, connection, IsFileCopy: false);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            connection.Dispose();
            throw new SourceException($"Could not connect to {name}. Check the source's connection string in sources.json.");
        }
    }

    private string Help()
    {
        var names = catalog.Names();
        var named = names.Count == 0 ? $"none defined yet in {catalog.FilePath}" : string.Join(", ", names);
        return "A source is a .csv, .tsv, .xlsx, .xlsm, .xlsb, .xls, .db or .sqlite file path, a folder of CSV and Excel " +
            $"files, or a named source ({named}).";
    }
}
