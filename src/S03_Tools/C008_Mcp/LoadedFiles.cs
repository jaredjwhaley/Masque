using Masque.Core.Tables;
using Masque.Sqlite;
using Microsoft.Data.Sqlite;

namespace Masque.Mcp;

/// <summary>A file that a write was saved to.</summary>
/// <param name="Path">The file.</param>
/// <param name="BackupPath">The backup holding its previous version.</param>
public sealed record SavedFile(string Path, string BackupPath);

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

    /// <summary>Saves every change made to the copy back to its file, leaving files with no changes alone.</summary>
    /// <returns>The files saved, each with its backup.</returns>
    /// <exception cref="TableFileException">A change can't be saved; that file is left as it was.</exception>
    public IReadOnlyList<SavedFile> SaveChanges()
    {
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
                saved.Add(new SavedFile(file.Path, file.Save(perTable)));
            }
        }

        return saved;
    }
}
