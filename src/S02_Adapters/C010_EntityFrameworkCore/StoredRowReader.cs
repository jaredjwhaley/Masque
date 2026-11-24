using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Masque.EntityFrameworkCore;

/// <summary>One key column's condition: the column must hold one of the given values.</summary>
/// <param name="Column">The key column's name in the table.</param>
/// <param name="Values">
/// The values it may hold: the key's value, or, for a masked key holding a token, every form its real value may be
/// stored in.
/// </param>
internal sealed record KeyCondition(string Column, IReadOnlyList<object> Values);

/// <summary>
/// Reads what a tracked entity's row stores, straight from the database, so a save can find the row behind a masked
/// key and keep the spelling the row already has. What it reads is used only to steer the save and never reaches the
/// caller.
/// </summary>
internal static class StoredRowReader
{
    /// <summary>The most rows read: two are enough to know the key doesn't pick out one row.</summary>
    private const int RowLimit = 2;

    /// <summary>The reserved prefix of this reader's parameter names.</summary>
    private const string ParameterPrefix = "masque_row_";

    /// <summary>
    /// Reads <paramref name="columns"/> from the rows of <paramref name="table"/> that meet every key condition, on
    /// the context's own connection and transaction. The connection must be open.
    /// </summary>
    /// <param name="context">The context being saved.</param>
    /// <param name="table">The entity's table.</param>
    /// <param name="keys">One condition per key column.</param>
    /// <param name="columns">The columns to read, in order.</param>
    /// <returns>Up to two rows, each holding the columns' values in order (null for a database NULL).</returns>
    public static IReadOnlyList<object?[]> Read(
        DbContext context, StoreObjectIdentifier table, IReadOnlyList<KeyCondition> keys, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfZero(keys.Count);
        ArgumentOutOfRangeException.ThrowIfZero(columns.Count);

        // The provider's own quoting and parameter syntax, so the query is valid on SQLite and SQL Server alike.
        var sql = context.GetService<ISqlGenerationHelper>();

        using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();

        var conditions = new List<string>(keys.Count);
        var next = 0;
        foreach (var key in keys)
        {
            var placeholders = new List<string>(key.Values.Count);
            foreach (var value in key.Values)
            {
                var name = ParameterPrefix + next++;
                var parameter = command.CreateParameter();
                parameter.ParameterName = sql.GenerateParameterName(name);
                parameter.Value = value;
                command.Parameters.Add(parameter);
                placeholders.Add(sql.GenerateParameterNamePlaceholder(name));
            }

            conditions.Add($"{sql.DelimitIdentifier(key.Column)} IN ({string.Join(", ", placeholders)})");
        }

        command.CommandText =
            $"SELECT {string.Join(", ", columns.Select(c => sql.DelimitIdentifier(c)))} " +
            $"FROM {sql.DelimitIdentifier(table.Name, table.Schema)} " +
            $"WHERE {string.Join(" AND ", conditions)}";

        var rows = new List<object?[]>(RowLimit);
        using var reader = command.ExecuteReader();
        while (rows.Count < RowLimit && reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }
}
