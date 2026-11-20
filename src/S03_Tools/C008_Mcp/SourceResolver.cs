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
/// <param name="Files">
/// For CSV and Excel data opened for writing: the files behind the in-memory copy, to save writes back to.
/// Null for databases, and for files opened only to read.
/// </param>
public sealed record OpenedSource(string Name, DbConnection Connection, LoadedFiles? Files = null) : IDisposable
{
    /// <inheritdoc />
    public void Dispose() => Connection.Dispose();
}

/// <summary>Turns the text an assistant passes as "source" into something Masque can query.</summary>
public interface ISourceResolver
{
    /// <summary>Opens a source.</summary>
    /// <param name="source">A name from sources.json, a CSV/Excel/SQLite file path, or a folder path.</param>
    /// <param name="writable">
    /// Whether the caller will write. CSV and Excel data is then loaded into a writable copy whose changes can be saved
    /// back to the files; otherwise the copy is read-only.
    /// </param>
    /// <returns>An open source; the caller disposes it.</returns>
    /// <exception cref="SourceException">Nothing matches, or the file type isn't supported.</exception>
    OpenedSource Open(string source, bool writable = false);
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
    public OpenedSource Open(string source, bool writable = false)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new SourceException("No source given. " + Help());
        }

        source = source.Trim();

        if (catalog.TryGet(source, out var entry))
        {
            return OpenNamed(source, entry, writable);
        }

        if (Directory.Exists(source))
        {
            return OpenFolder(source, writable);
        }

        if (File.Exists(source))
        {
            return OpenFile(source, writable);
        }

        throw new SourceException($"No source matches {source}. " + Help());
    }

    private OpenedSource OpenNamed(string name, SourceEntry entry, bool writable) => entry.Type switch
    {
        SourceType.SqlServer when !string.IsNullOrWhiteSpace(entry.ConnectionString) => OpenSqlServer(name, entry.ConnectionString),
        SourceType.Sqlite when !string.IsNullOrWhiteSpace(entry.Path) => OpenSqlite(entry.Path),
        SourceType.File when !string.IsNullOrWhiteSpace(entry.Path) =>
            Directory.Exists(entry.Path) ? OpenFolder(entry.Path, writable) : OpenFile(entry.Path, writable),
        _ => throw new SourceException(
            $"Source {name} in {catalog.FilePath} is incomplete: sqlServer needs a connectionString; sqlite and file need a path."),
    };

    private OpenedSource OpenFile(string path, bool writable)
    {
        var extension = Path.GetExtension(path);

        if (SqliteSources.Extensions.Contains(extension))
        {
            return OpenSqlite(path);
        }

        if (CsvExtensions.Contains(extension))
        {
            return Load(Path.GetFileName(path), [.. AsLoaded(CsvFile.Open(path))], writable);
        }

        if (ExcelWorkbook.Extensions.Contains(extension))
        {
            // A single workbook's tables are its sheets, named as in Excel.
            return Load(Path.GetFileName(path), [.. AsLoaded(ExcelFile.Open(path))], writable);
        }

        throw new SourceException($"Masque can't read {extension} files. " + Help());
    }

    private static OpenedSource OpenFolder(string folder, bool writable)
    {
        var tables = new List<LoadedTable>();
        foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var extension = Path.GetExtension(file);
            if (CsvExtensions.Contains(extension))
            {
                tables.AddRange(AsLoaded(CsvFile.Open(file)));
            }
            else if (ExcelWorkbook.Extensions.Contains(extension))
            {
                tables.AddRange(WorkbookTablesInFolder(ExcelFile.Open(file)));
            }
        }

        if (tables.Count == 0)
        {
            throw new SourceException($"The folder {folder} has no CSV or Excel files.");
        }

        return Load(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), tables, writable);
    }

    /// <summary>A file's tables under their own names.</summary>
    private static IEnumerable<LoadedTable> AsLoaded(ITableFile file) =>
        file.Tables.Select((table, i) => new LoadedTable(file, i, table));

    /// <summary>In a folder, a one-sheet workbook is named after its file; sheets of a larger workbook are "file_sheet".</summary>
    private static IEnumerable<LoadedTable> WorkbookTablesInFolder(ExcelFile workbook)
    {
        var stem = Path.GetFileNameWithoutExtension(workbook.Path);
        var sheets = workbook.Tables;
        return sheets.Select((sheet, i) => new LoadedTable(
            workbook, i, sheet with { Name = sheets.Count == 1 ? stem : $"{stem}_{sheet.Name}" }));
    }

    /// <summary>Checks every table has a real header row (before names are made SQL-safe), then loads them.</summary>
    private static OpenedSource Load(string name, IReadOnlyList<LoadedTable> tables, bool writable)
    {
        foreach (var table in tables)
        {
            HeaderRow.EnsureLooksLikeHeaders(table.Table.Name, table.Table.Headers);
        }

        if (!writable)
        {
            return new OpenedSource(name, SqliteSources.LoadTables(tables.Select(t => t.Table)));
        }

        var files = new LoadedFiles(tables);
        return new OpenedSource(name, files.Connection, files);
    }

    private static OpenedSource OpenSqlite(string path) => new(Path.GetFileName(path), SqliteSources.OpenDatabase(path));

    /// <summary>Opens a SQL Server source; connection failures name the source, never the connection string.</summary>
    private static OpenedSource OpenSqlServer(string name, string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            connection.Open();
            return new OpenedSource(name, connection);
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
