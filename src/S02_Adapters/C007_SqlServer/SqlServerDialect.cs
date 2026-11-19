using System.Data;
using System.Data.Common;
using Masque.Core.Query;
using Masque.Data.Dialects;
using Microsoft.Data.SqlClient;

namespace Masque.SqlServer;

/// <summary>
/// Masque's dialect for SQL Server (Microsoft.Data.SqlClient). Lineage comes from browse mode
/// (<see cref="CommandBehavior.KeyInfo"/>), which reports each result column's base table and column; the key columns
/// it adds are hidden. SQL Server has no per-connection guard like SQLite's authorizer, so give Masque a login with
/// only the rights its callers should have.
/// </summary>
public sealed class SqlServerDialect : MasqueDialect
{
    /// <summary>The schema SQL Server assumes when a statement names none.</summary>
    public const string DefaultSchema = "dbo";

    /// <summary>
    /// Errors about the query text itself (syntax, unknown names, grouping), whose messages only repeat what the caller
    /// wrote. Every other error could quote data (a failed conversion names the value), so its message is hidden.
    /// </summary>
    private static readonly HashSet<int> QueryTextErrors =
    [
        102, 105, 145, 156, 195, 207, 208, 209, 229, 319, 336, 1033, 1038, 4104, 4106, 4108, 4109, 4145, 4147, 8120, 8127, 8155,
    ];

    /// <inheritdoc />
    public override CommandBehavior LineageBehavior => CommandBehavior.KeyInfo;

    /// <inheritdoc />
    public override bool Supports(DbConnection connection) => connection is SqlConnection;

    /// <inheritdoc />
    public override IReadOnlyList<TableReference> ListTables(DbConnection connection, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_SCHEMA, TABLE_NAME";

        using var reader = command.ExecuteReader();
        var tables = new List<TableReference>();
        while (reader.Read())
        {
            tables.Add(new TableReference(reader.GetString(0), reader.GetString(1)));
        }

        return tables;
    }

    /// <summary>Fills in <c>dbo</c> when a statement names no schema.</summary>
    /// <param name="table">A table name.</param>
    /// <returns>The schema-qualified name.</returns>
    public override TableReference Normalize(TableReference table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table.Schema is null ? table with { Schema = DefaultSchema } : table;
    }

    /// <inheritdoc />
    public override string SampleSql(TableReference table, int rows)
    {
        ArgumentNullException.ThrowIfNull(table);
        var normalized = Normalize(table);
        return $"SELECT TOP ({Math.Max(0, rows)}) * FROM {Bracket(normalized.Schema!)}.{Bracket(normalized.Name)}";
    }

    /// <inheritdoc />
    public override QueryFailedException Describe(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SqlException sql
            ? new QueryFailedException(QueryTextErrors.Contains(sql.Number)
                ? $"SQL Server error {sql.Number}: {sql.Message}"
                : $"SQL Server error {sql.Number} (severity {sql.Class}). Details are hidden because they could include data.")
            : new QueryFailedException("The database reported an error. Details are hidden because they could include data.");
    }

    private static string Bracket(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
}
