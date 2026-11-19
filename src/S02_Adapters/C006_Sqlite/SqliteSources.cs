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
    /// Loads tables into a new in-memory database and makes it read-only, since changes to a copy of a file would
    /// be lost anyway. Numbers load as numbers; blank cells load as NULL.
    /// </summary>
    /// <param name="tables">The tables, each with a header row.</param>
    /// <returns>The open connection; the caller owns it.</returns>
    public static SqliteConnection LoadTables(IEnumerable<TableData> tables)
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

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
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
