using System.Data;
using System.Data.Common;
using Masque.Core.Query;

namespace Masque.Data.Dialects;

/// <summary>
/// Shared parts of a dialect: reading column lineage from <see cref="DbDataReader.GetSchemaTable"/>, which ADO.NET
/// providers fill in the same way. Database packages derive from this and add what differs.
/// </summary>
public abstract class MasqueDialect : IMasqueDialect
{
    /// <inheritdoc />
    public virtual CommandBehavior LineageBehavior => CommandBehavior.Default;

    /// <inheritdoc />
    public abstract bool Supports(DbConnection connection);

    /// <inheritdoc />
    public abstract IReadOnlyList<TableReference> ListTables(DbConnection connection, DbTransaction? transaction);

    /// <inheritdoc />
    public abstract TableReference Normalize(TableReference table);

    /// <inheritdoc />
    public abstract string SampleSql(TableReference table, int rows);

    /// <inheritdoc />
    public abstract QueryFailedException Describe(DbException exception);

    /// <inheritdoc />
    /// <remarks>The base dialect adds no guards.</remarks>
    public virtual void Secure(DbConnection connection, Func<bool> writesAllowed)
    {
    }

    /// <inheritdoc />
    public virtual IReadOnlyList<ResultColumn> ReadColumns(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.FieldCount == 0)
        {
            return [];
        }

        using var schema = reader.GetSchemaTable();
        if (schema is null)
        {
            // No lineage at all: every column is treated as computed, which masks text (fail closed).
            return Enumerable.Range(0, reader.FieldCount).Select(i => new ResultColumn(reader.GetName(i), i, null, null)).ToList();
        }

        var columns = new List<ResultColumn>();
        foreach (DataRow row in schema.Rows)
        {
            // Columns the database added for itself (SQL Server's browse-mode keys) are not part of the result.
            if (Flag(row, "IsHidden"))
            {
                continue;
            }

            var ordinal = Convert.ToInt32(row[SchemaTableColumn.ColumnOrdinal], System.Globalization.CultureInfo.InvariantCulture);
            var name = Text(row, SchemaTableColumn.ColumnName) ?? reader.GetName(ordinal);
            var table = Text(row, SchemaTableColumn.BaseTableName);
            var column = Text(row, SchemaTableColumn.BaseColumnName);

            columns.Add(Flag(row, SchemaTableColumn.IsExpression) || table is null || column is null
                ? new ResultColumn(name, ordinal, null, null)
                : new ResultColumn(name, ordinal, Normalize(new TableReference(Text(row, SchemaTableColumn.BaseSchemaName), table)), column));
        }

        return columns;
    }

    /// <summary>Reads a text cell of a schema row, treating a missing column, null or empty text as absent.</summary>
    private static string? Text(DataRow row, string column) =>
        row.Table.Columns.Contains(column) && row[column] is string { Length: > 0 } text ? text : null;

    /// <summary>Reads a true/false cell of a schema row; a missing column or null counts as false.</summary>
    private static bool Flag(DataRow row, string column) => row.Table.Columns.Contains(column) && row[column] is true;
}
