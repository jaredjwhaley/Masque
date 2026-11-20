using Masque.Core.Tables;
using Microsoft.Data.Sqlite;

namespace Masque.Sqlite;

/// <summary>
/// Works out what a write did to file tables loaded by <see cref="TableLoader"/>, by comparing each table's rows now
/// with the values the file's text loaded as. Rows are matched by rowid: the loader inserts rows in file order, so
/// rowids 1 to n are the file's rows, a missing one was deleted, and any higher one was inserted. (SQLite may give a
/// new row the rowid of a deleted last row; that reads as the row being changed in place, which saves the same data.)
/// </summary>
internal static class TableChangeReader
{
    /// <summary>Reads the changes to every table.</summary>
    /// <param name="connection">The connection the tables were loaded into.</param>
    /// <param name="tables">The tables exactly as they were loaded.</param>
    /// <returns>One entry per table, in the same order.</returns>
    /// <exception cref="TableFileException">A changed cell holds a binary value, which no file format here can store.</exception>
    public static IReadOnlyList<TableChanges> Read(SqliteConnection connection, IReadOnlyList<TableData> tables)
    {
        var shapes = TableLoader.Shapes(tables);
        return tables.Select((table, i) => ReadTable(connection, shapes[i], table)).ToList();
    }

    private static TableChanges ReadTable(SqliteConnection connection, TableShape shape, TableData table)
    {
        var updates = new List<CellChange>();
        var inserted = new List<IReadOnlyList<object?>>();
        var seen = new bool[table.Rows.Count];

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {RowIdAlias(shape)}, {string.Join(", ", shape.Columns.Select(SqlName.Quote))} FROM {SqlName.Quote(shape.Name)} ORDER BY 1";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var rowId = reader.GetInt64(0);
            var values = Enumerable.Range(1, shape.Columns.Count).Select(i => Value(reader, i, shape.Name)).ToList();

            if (rowId < 1 || rowId > table.Rows.Count)
            {
                inserted.Add(values);
                continue;
            }

            var row = (int)rowId - 1;
            seen[row] = true;
            var original = table.Rows[row];
            for (var column = 0; column < values.Count; column++)
            {
                var loaded = TableLoader.ToStored(column < original.Count ? original[column] : null, shape.Types[column]);
                if (!Equals(values[column], loaded is DBNull ? null : loaded))
                {
                    updates.Add(new CellChange(row, column, values[column]));
                }
            }
        }

        var deleted = Enumerable.Range(0, seen.Length).Where(row => !seen[row]).ToList();
        return updates.Count == 0 && deleted.Count == 0 && inserted.Count == 0
            ? TableChanges.None
            : new TableChanges(updates, deleted, inserted);
    }

    /// <summary>A cell's value as a string, long, double or null. Binary values are refused without quoting them.</summary>
    private static object? Value(SqliteDataReader reader, int ordinal, string table)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DBNull => null,
            string or long or double => value,
            _ => throw new TableFileException(
                $"A row in {table} now holds a binary value, which can't be saved to a CSV or Excel file. Nothing was saved."),
        };
    }

    /// <summary>A name for the rowid that no column hides: SQLite has three, and a column can take any of them.</summary>
    private static string RowIdAlias(TableShape shape)
    {
        foreach (var alias in new[] { "_rowid_", "rowid", "oid" })
        {
            if (!shape.Columns.Contains(alias, StringComparer.OrdinalIgnoreCase))
            {
                return alias;
            }
        }

        throw new TableFileException(
            $"{shape.Name} has columns named _rowid_, rowid and oid, so Masque can't tell its rows apart to save changes. Rename one in the file.");
    }
}
