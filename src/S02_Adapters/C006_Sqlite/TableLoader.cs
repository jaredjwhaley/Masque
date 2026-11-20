using System.Globalization;
using Masque.Core.Tables;
using Microsoft.Data.Sqlite;

namespace Masque.Sqlite;

/// <summary>
/// Copies file tables into a SQLite database. Every value in a CSV or sheet arrives as text, so each column is
/// typed from its values: whole numbers load as INTEGER, other plain numbers as REAL (so sums and comparisons
/// work), and anything else, including numbers with leading zeros such as ZIP codes, stays TEXT.
/// </summary>
internal static class TableLoader
{
    private const NumberStyles Numeric = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
        | NumberStyles.AllowThousands | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;

    /// <summary>The SQLite type a column loads as.</summary>
    internal enum ColumnType
    {
        Text,
        Integer,
        Real,
    }

    /// <summary>Works out each table's SQL name, column names and column types. The same tables always give the same shapes.</summary>
    /// <param name="tables">The tables to load.</param>
    /// <returns>One shape per table, in the same order.</returns>
    public static IReadOnlyList<TableShape> Shapes(IReadOnlyList<TableData> tables)
    {
        var tableNames = SqlName.MakeUnique(tables.Select(t => t.Name), "Table");
        return tables.Select((table, i) => new TableShape(
            tableNames[i],
            SqlName.MakeUnique(table.Headers, "Column"),
            table.ColumnSamples(table.Rows.Count).Select(InferType).ToList())).ToList();
    }

    /// <summary>Creates and fills one table per input, with sanitized, unique table and column names.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="tables">The tables to load.</param>
    public static void Load(SqliteConnection connection, IReadOnlyList<TableData> tables)
    {
        var shapes = Shapes(tables);

        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < tables.Count; i++)
        {
            LoadTable(connection, transaction, shapes[i], tables[i]);
        }

        transaction.Commit();
    }

    /// <summary>The value a cell's text is stored as in a column of the given type: a number, the text, or NULL when blank.</summary>
    /// <param name="value">The cell's text.</param>
    /// <param name="type">The column's type.</param>
    /// <returns>A <see cref="long"/>, <see cref="double"/>, <see cref="string"/> or <see cref="DBNull"/>.</returns>
    public static object ToStored(string? value, ColumnType type)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DBNull.Value;
        }

        return type switch
        {
            ColumnType.Integer => long.Parse(value, Numeric & ~NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
            ColumnType.Real => double.Parse(value, Numeric, CultureInfo.InvariantCulture),
            _ => value,
        };
    }

    private static void LoadTable(SqliteConnection connection, SqliteTransaction transaction, TableShape shape, TableData table)
    {
        var columns = shape.Columns;

        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            var definitions = columns.Select((column, i) => $"{SqlName.Quote(column)} {shape.Types[i].ToString().ToUpperInvariant()}");
            create.CommandText = $"CREATE TABLE {SqlName.Quote(shape.Name)} ({string.Join(", ", definitions)})";
            create.ExecuteNonQuery();
        }

        // Rows go in in file order, so each row's rowid is its position plus one; TableChangeReader relies on that.
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"INSERT INTO {SqlName.Quote(shape.Name)} VALUES ({string.Join(", ", columns.Select((_, i) => $"$p{i}"))})";
        var parameters = columns.Select((_, i) => insert.Parameters.Add($"$p{i}", SqliteType.Text)).ToList();

        foreach (var row in table.Rows)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                parameters[i].Value = ToStored(i < row.Count ? row[i] : null, shape.Types[i]);
            }

            insert.ExecuteNonQuery();
        }
    }

    /// <summary>The narrowest type every non-blank value fits; blank columns are text.</summary>
    private static ColumnType InferType(IReadOnlyList<string?> values)
    {
        var present = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        if (present.Count == 0 || present.Any(HasLeadingZero))
        {
            return ColumnType.Text;
        }

        if (present.All(v => long.TryParse(v, Numeric & ~NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _)))
        {
            return ColumnType.Integer;
        }

        return present.All(v => double.TryParse(v, Numeric, CultureInfo.InvariantCulture, out _)) ? ColumnType.Real : ColumnType.Text;
    }

    /// <summary>"02134" or "-007": a leading zero that a number would drop. "0" and "0.5" don't count.</summary>
    private static bool HasLeadingZero(string value)
    {
        var digits = value.TrimStart('-', '+');
        return digits.Length > 1 && digits[0] == '0' && char.IsAsciiDigit(digits[1]);
    }
}

/// <summary>How one file table is laid out in SQLite.</summary>
/// <param name="Name">The SQL table name.</param>
/// <param name="Columns">The SQL column names, one per header.</param>
/// <param name="Types">Each column's type.</param>
internal sealed record TableShape(string Name, IReadOnlyList<string> Columns, IReadOnlyList<TableLoader.ColumnType> Types);
