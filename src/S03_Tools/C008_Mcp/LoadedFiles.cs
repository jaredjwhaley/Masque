using Masque.Core.Tables;
using Masque.Sqlite;
using Microsoft.Data.Sqlite;

namespace Masque.Mcp;

/// <summary>A file that a write was saved to: either the source itself (with a backup) or a new copy of it.</summary>
/// <param name="Path">The file written.</param>
/// <param name="BackupPath">When the source was replaced: the backup holding its previous version. Null for a copy.</param>
/// <param name="CopiedFrom">When a copy was written: the source it was made from, left unchanged. Null otherwise.</param>
public sealed record SavedFile(string Path, string? BackupPath, string? CopiedFrom = null);

/// <summary>One loaded table and where it came from.</summary>
/// <param name="File">The file it was read from.</param>
/// <param name="Index">Its position among that file's tables.</param>
/// <param name="Table">The table as loaded (possibly renamed, as tables from a folder are).</param>
public sealed record LoadedTable(ITableFile File, int Index, TableData Table);

/// <summary>
/// CSV and Excel files loaded into one writable in-memory copy. After a write to the copy, <see cref="SaveChanges"/>
/// works out what changed and saves it to the files the changed tables came from.
/// </summary>
public sealed class LoadedFiles
{
    /// <summary>Why saveAs needs a single file; shared with the tool's up-front check.</summary>
    internal const string OneFileForSaveAs =
        "saveAs works only when the source is one CSV or Excel file, since one copy name can't stand for several files or a database. Nothing was changed.";

    private readonly SqliteConnection _connection;
    private readonly IReadOnlyList<LoadedTable> _tables;

    /// <summary>Loads the tables into a new writable in-memory copy.</summary>
    /// <param name="tables">Every table to load, in load order, with the file each came from.</param>
    public LoadedFiles(IReadOnlyList<LoadedTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _tables = tables;
        _connection = SqliteSources.LoadTables(tables.Select(t => t.Table), writable: true);
    }

    /// <summary>The in-memory copy. Its owner (the <see cref="OpenedSource"/>) disposes it.</summary>
    public SqliteConnection Connection => _connection;

    /// <summary>How many files the tables came from (a folder can hold several).</summary>
    public int FileCount => _tables.Select(t => t.File).Distinct().Count();

    /// <summary>
    /// Saves every change made to the in-memory copy, leaving files with no changes alone: back to each changed file
    /// (with a backup), or, when <paramref name="saveAs"/> is given, to a new file beside the one source file.
    /// </summary>
    /// <param name="saveAs">The new file's name or path, in the source's folder; null to save to the source.</param>
    /// <returns>The files saved; empty when nothing changed.</returns>
    /// <exception cref="TableFileException">
    /// A change can't be saved, or <paramref name="saveAs"/> is given for tables from several files. No file is changed.
    /// </exception>
    public IReadOnlyList<SavedFile> SaveChanges(string? saveAs = null)
    {
        if (saveAs is not null && FileCount != 1)
        {
            throw new TableFileException(OneFileForSaveAs);
        }

        var changes = SqliteSources.ReadChanges(_connection, [.. _tables.Select(t => t.Table)]);

        var saved = new List<SavedFile>();
        foreach (var file in _tables.Select(t => t.File).Distinct())
        {
            var perTable = file.Tables.Select(_ => TableChanges.None).ToArray();
            for (var i = 0; i < _tables.Count; i++)
            {
                if (ReferenceEquals(_tables[i].File, file))
                {
                    perTable[_tables[i].Index] = changes[i];
                }
            }

            if (perTable.Any(change => !change.IsEmpty))
            {
                saved.Add(saveAs is null
                    ? new SavedFile(file.Path, file.Save(perTable))
                    : new SavedFile(file.SaveCopy(perTable, saveAs), BackupPath: null, CopiedFrom: file.Path));
            }
        }

        return saved;
    }
}
