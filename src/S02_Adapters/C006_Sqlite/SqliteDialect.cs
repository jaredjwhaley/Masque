using System.Data.Common;
using Masque.Core.Query;
using Masque.Data.Dialects;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Masque.Sqlite;

/// <summary>
/// Masque's dialect for SQLite (Microsoft.Data.Sqlite). Besides listing and sampling tables, it installs an SQLite
/// authorizer on each wrapped connection: a callback SQLite asks before compiling any statement, which refuses schema
/// changes, other databases, file and extension functions, and any write Masque didn't check.
/// </summary>
public sealed class SqliteDialect : MasqueDialect
{
    // SQLITE_ERROR (1) is a problem with the query text itself ("no such column: x"), so its message only repeats
    // what the caller wrote. Other codes could describe data, so their messages are hidden.
    private const int SqliteError = 1;
    private const int SqliteReadOnly = 8;
    private const int SqliteAuth = 23;

    /// <summary>Functions that reach files or load code; denied even if the SQL check missed them.</summary>
    private static readonly HashSet<string> DeniedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "load_extension", "readfile", "writefile", "edit", "fts3_tokenizer",
    };

    /// <inheritdoc />
    public override bool Supports(DbConnection connection) => connection is SqliteConnection;

    /// <inheritdoc />
    public override IReadOnlyList<TableReference> ListTables(DbConnection connection, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name";

        using var reader = command.ExecuteReader();
        var tables = new List<TableReference>();
        while (reader.Read())
        {
            tables.Add(new TableReference(null, reader.GetString(0)));
        }

        return tables;
    }

    /// <summary>Tables in the main database have no schema; any other schema (temp, attached) stays and isn't listed.</summary>
    /// <param name="table">A table name.</param>
    /// <returns>The name without <c>main.</c>.</returns>
    public override TableReference Normalize(TableReference table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return string.Equals(table.Schema, "main", StringComparison.OrdinalIgnoreCase) ? table with { Schema = null } : table;
    }

    /// <inheritdoc />
    public override string SampleSql(TableReference table, int rows)
    {
        ArgumentNullException.ThrowIfNull(table);
        return $"SELECT * FROM {SqlName.Quote(table.Name)} LIMIT {Math.Max(0, rows)}";
    }

    /// <inheritdoc />
    public override QueryFailedException Describe(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var code = exception is SqliteException sqlite ? sqlite.SqliteErrorCode : 0;
        return new QueryFailedException(code switch
        {
            SqliteError => exception.Message,
            SqliteReadOnly => "The database is read-only.",
            SqliteAuth => "Masque's connection guard refused the statement (a schema change, file access or unchecked write).",
            _ => $"SQLite error {code}. Details are hidden because they could include data.",
        });
    }

    /// <summary>Installs the authorizer and stops other databases from being attached.</summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <param name="writesAllowed">Says whether a checked write is running right now.</param>
    public override void Secure(DbConnection connection, Func<bool> writesAllowed)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(writesAllowed);
        var handle = ((SqliteConnection)connection).Handle;

        raw.sqlite3_limit(handle, raw.SQLITE_LIMIT_ATTACHED, 0);
        raw.sqlite3_set_authorizer(handle, (_, action, _, second, _, _) => Authorize(action, second, writesAllowed), null);
    }

    /// <summary>SQLite's authorizer callback: returns <c>SQLITE_OK</c> or <c>SQLITE_DENY</c> for one action.</summary>
    /// <param name="action">The action code (sqlite3.h <c>SQLITE_CREATE_TABLE</c> and so on).</param>
    /// <param name="second">The action's second detail (for a function call, its name).</param>
    /// <param name="writesAllowed">Whether a checked write is running.</param>
    private static int Authorize(int action, string? second, Func<bool> writesAllowed)
    {
        return action switch
        {
            // Schema changes: CREATE (1-8), DROP (10-17), ALTER (26), REINDEX (27), ANALYZE (28), virtual tables (29-30).
            >= raw.SQLITE_CREATE_INDEX and <= raw.SQLITE_CREATE_VIEW => raw.SQLITE_DENY,
            >= raw.SQLITE_DROP_INDEX and <= raw.SQLITE_DROP_VIEW => raw.SQLITE_DENY,
            >= raw.SQLITE_ALTER_TABLE and <= raw.SQLITE_DROP_VTABLE => raw.SQLITE_DENY,

            // Other databases.
            raw.SQLITE_ATTACH or raw.SQLITE_DETACH => raw.SQLITE_DENY,

            // Writes run only while Masque has checked and allowed them.
            raw.SQLITE_INSERT or raw.SQLITE_UPDATE or raw.SQLITE_DELETE => writesAllowed() ? raw.SQLITE_OK : raw.SQLITE_DENY,

            raw.SQLITE_FUNCTION when second is not null && DeniedFunctions.Contains(second) => raw.SQLITE_DENY,

            // Reads, selects, transactions, savepoints and PRAGMA (Microsoft.Data.Sqlite uses it internally).
            _ => raw.SQLITE_OK,
        };
    }
}
