using System.Collections.Frozen;

namespace Masque.Core.Query;

/// <summary>The keyword lists <see cref="SqlStatement"/> reads SQL with.</summary>
internal static class SqlKeywords
{
    /// <summary>
    /// Words refused anywhere outside quotes: they change schema, permissions or session state, run code or reach
    /// files and other databases, start a second statement (SQL Server doesn't need semicolons between them), or
    /// reshape results so a column no longer comes from one source column.
    /// </summary>
    public static readonly FrozenSet<string> Forbidden = new[]
    {
        "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME", "GRANT", "REVOKE", "DENY",
        "EXEC", "EXECUTE", "CALL", "ATTACH", "DETACH", "PRAGMA", "VACUUM", "REINDEX", "ANALYZE",
        "LOAD_EXTENSION", "READFILE", "WRITEFILE", "FTS3_TOKENIZER",
        "OPENROWSET", "OPENDATASOURCE", "OPENQUERY", "OPENXML", "BULK",
        "DBCC", "WAITFOR", "DECLARE", "SHUTDOWN", "KILL", "BACKUP", "RESTORE", "CHECKPOINT", "RECONFIGURE",
        "BEGIN", "COMMIT", "ROLLBACK", "SAVE", "TRAN", "TRANSACTION", "USE", "GO", "PRINT", "RAISERROR", "THROW",
        "IF", "WHILE", "GOTO", "RETURN", "BREAK", "CONTINUE", "OPEN", "CLOSE", "DEALLOCATE", "SETUSER",
        "READTEXT", "WRITETEXT", "UPDATETEXT",
        "MERGE", "UPSERT", "RETURNING", "OUTPUT", "CONFLICT", "PIVOT", "UNPIVOT", "FOR",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set operators: a result column would take its lineage from the first SELECT only.</summary>
    public static readonly FrozenSet<string> SetOperators = new[] { "UNION", "INTERSECT", "EXCEPT", "MINUS" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Words that are never a bare column or alias name (both databases reserve them). Any other word may be a name,
    /// so a masked column called <c>Last</c> or <c>Date</c> is still checked.
    /// </summary>
    public static readonly FrozenSet<string> Reserved = new[]
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "BETWEEN", "EXISTS", "AS",
        "ON", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "GROUP", "BY", "HAVING", "ORDER",
        "ASC", "DESC", "TOP", "DISTINCT", "ALL", "ANY", "SOME", "CASE", "WHEN", "THEN", "ELSE", "END",
        "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "WITH", "ESCAPE", "COLLATE", "OVER", "DEFAULT",
    }.Concat(Forbidden).Concat(SetOperators).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Unreserved words that start a clause, so they are read as keywords rather than names.</summary>
    public static readonly FrozenSet<string> ClauseWords = new[]
    {
        "LIMIT", "OFFSET", "FETCH", "WINDOW", "USING", "APPLY", "PARTITION", "NATURAL",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a token can be a table, column or alias name.</summary>
    public static bool IsName(SqlToken token) =>
        token.Kind == SqlTokenKind.QuotedName
        || (token.Kind == SqlTokenKind.Word && !Reserved.Contains(token.Text) && !ClauseWords.Contains(token.Text));
}
