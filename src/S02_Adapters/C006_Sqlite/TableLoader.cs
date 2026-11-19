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

    private enum ColumnType
    {
        Text,
        Integer,
        Real,
    }

    /// <summary>Creates and fills one table per input, with sanitized, unique table and column names.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="tables">The tables to load.</param>
    public static void Load(SqliteConnection connection, IReadOnlyList<TableData> tables)
    {
        var tableNames = SqlName.MakeUnique(tables.Select(t => t.Name), "Table");

        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < tables.Count; i++)
        {
            LoadTable(connection, transaction, tableNames[i], tables[i]);
        }

        transaction.Commit();
    }

    private static void LoadTable(SqliteConnection connection, SqliteTransaction transaction, string name, TableData table)
    {
        var columns = SqlName.MakeUnique(table.Headers, "Column");
        var samples = table.ColumnSamples(table.Rows.Count);
        var types = samples.Select(InferType).ToList();

        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            var definitions = columns.Select((column, i) => $"{SqlName.Quote(column)} {types[i].ToString().ToUpperInvariant()}");
            create.CommandText = $"CREATE TABLE {SqlName.Quote(name)} ({string.Join(", ", definitions)})";
            create.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"INSERT INTO {SqlName.Quote(name)} VALUES ({string.Join(", ", columns.Select((_, i) => $"$p{i}"))})";
        var parameters = columns.Select((_, i) => insert.Parameters.Add($"$p{i}", SqliteType.Text)).ToList();

        foreach (var row in table.Rows)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                parameters[i].Value = ToStored(i < row.Count ? row[i] : null, types[i]);
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

    private static object ToStored(string? value, ColumnType type)
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
}
