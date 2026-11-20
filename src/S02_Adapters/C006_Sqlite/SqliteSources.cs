using Masque.Core.Query;
using Masque.Core.Tables;
using Microsoft.Data.Sqlite;

namespace Masque.Sqlite;

/// <summary>
/// Opens SQLite connections for Masque to wrap: an existing database file, or CSV and Excel tables loaded into a
/// private in-memory database. Wrap the result with a <see cref="Masque.Data.MasqueGate"/> before querying it.
/// </summary>
public static class SqliteSources
{
    /// <summary>File extensions read as SQLite databases.</summary>
    public static readonly IReadOnlySet<string> Extensions =
        new HashSet<string>([".db", ".sqlite", ".sqlite3"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads tables into a new in-memory database. Numbers load as numbers; blank cells load as NULL. The copy is
    /// read-only unless <paramref name="writable"/> is set, for a caller that saves changes back to the files with
    /// <see cref="ReadChanges"/>.
    /// </summary>
    /// <param name="tables">The tables, each with a header row.</param>
    /// <param name="writable">Whether the copy accepts writes (Masque's own write checks still apply when wrapped).</param>
    /// <returns>The open connection; the caller owns it.</returns>
    public static SqliteConnection LoadTables(IEnumerable<TableData> tables, bool writable = false)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Pooling = false,
        }.ToString());

        try
        {
            connection.Open();
            TableLoader.Load(connection, tables.ToList());

            if (!writable)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA query_only = ON";
                command.ExecuteNonQuery();
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Works out what writes did to tables loaded by <see cref="LoadTables"/>: the cells that changed (with their new
    /// values), and the rows deleted and inserted, by their position in the file.
    /// </summary>
    /// <param name="connection">The connection the tables were loaded into, after the writes.</param>
    /// <param name="tables">The same tables, in the same order, as were loaded.</param>
    /// <returns>One entry per table, in the same order.</returns>
    /// <exception cref="TableFileException">A changed value can't be saved to a file (binary data).</exception>
    public static IReadOnlyList<TableChanges> ReadChanges(SqliteConnection connection, IReadOnlyList<TableData> tables)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tables);
        return TableChangeReader.Read(connection, tables);
    }

    /// <summary>Opens an existing SQLite database file. It is never created: a missing file is an error.</summary>
    /// <param name="path">The database file.</param>
    /// <returns>The open connection; the caller owns it.</returns>
    /// <exception cref="FileNotFoundException">The file doesn't exist.</exception>
    /// <exception cref="QueryFailedException">The file isn't a SQLite database.</exception>
    public static SqliteConnection OpenDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"File not found: {path}");
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());

        try
        {
            connection.Open();

            // Opening is lazy; reading the schema proves the file really is a database.
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema";
            command.ExecuteScalar();
            return connection;
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw new QueryFailedException($"Could not open {Path.GetFileName(path)} as a SQLite database (error {ex.SqliteErrorCode}).");
        }
    }
}
