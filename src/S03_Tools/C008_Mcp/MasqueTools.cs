using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Masque.Mcp;

/// <summary>
/// The MCP tools an assistant sees. Thin by design: each one hands off to <see cref="MasqueToolService"/> and
/// wraps the reply. The descriptions are the assistant's instructions, so they say how to use the tools well.
/// </summary>
/// <param name="service">Does the work.</param>
[McpServerToolType]
public sealed class MasqueTools(MasqueToolService service)
{
    /// <summary>Lists a source's tables and columns with their sensitivity categories.</summary>
    /// <param name="source">A source name, file path or folder path.</param>
    /// <returns>The tool result.</returns>
    [McpServerTool(Name = "masque_describe", Title = "Describe a data source (Masque)", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description("""
        Lists the tables and columns of a data source, with the sensitivity category Masque assigns each column
        (Name, Ssn, Email, Money, Code, ...). Shows no data values. Call this first for any CSV, Excel, SQLite or
        SQL Server data that may hold personal or health information, instead of reading the file directly.
        A table seen for the first time gets a proposed config that the user must review before queries return rows.
        """)]
    public CallToolResult Describe(
        [Description("A named source from the user's Masque sources.json, or a full path to a .csv, .tsv, .xlsx, .xlsm, .xlsb, .xls, .db or .sqlite file, or to a folder of CSV and Excel files (each file is a table).")]
        string source) => ToResult(service.Describe(source));

    /// <summary>Runs one SELECT and returns masked rows.</summary>
    /// <param name="source">A source name, file path or folder path.</param>
    /// <param name="sql">One SELECT statement.</param>
    /// <param name="maxRows">Most rows to return.</param>
    /// <param name="allowTypes">Types to show raw for this query, within what the policy allows.</param>
    /// <returns>The tool result.</returns>
    [McpServerTool(Name = "masque_query", Title = "Query data with masking (Masque)", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description("""
        Runs one SELECT against a data source and returns the rows with every sensitive value replaced by a
        consistent token (the same real value always gets the same token in this vault, so grouping, joining and
        comparing still work). CSV and Excel files are queried with SQLite SQL; a folder's files are tables you can
        join. SQL Server sources use T-SQL; name tables as schema.table.
        Each column in the reply says how it was treated: masked, real values (allowed raw), real values with any
        sensitive-looking value masked, or computed (numbers shown, text masked).
        Filters on a masked column: = and IN work, with real values or with tokens you were shown (Masque swaps a
        token for its real value). Joins on masked columns work. LIKE, ranges, functions and ORDER BY on a masked
        column are refused, because they would reveal the values.
        One statement only; SELECT or WITH.
        """)]
    public CallToolResult Query(
        [Description("Same as masque_describe's source.")] string source,
        [Description("One SELECT statement.")] string sql,
        [Description("Most rows to return, 1 to 1000. Prefer aggregates and small samples.")] int maxRows = 100,
        [Description("Optional: sensitive types to show raw for this query (for example [\"Email\"]). Only narrows what the Masque policy allows for the assistant; it never widens it. Leave out to get the policy's default.")]
        string[]? allowTypes = null) =>
        ToResult(service.Query(source, sql, maxRows, allowTypes));

    /// <summary>Runs one INSERT, UPDATE or DELETE through Masque's write checks.</summary>
    /// <param name="source">A named database source or a database file.</param>
    /// <param name="sql">One INSERT, UPDATE or DELETE statement.</param>
    /// <returns>The tool result.</returns>
    [McpServerTool(Name = "masque_execute", Title = "Change data through Masque", ReadOnly = false, Destructive = true,
        Idempotent = false, OpenWorld = false)]
    [Description("""
        Runs one INSERT, UPDATE or DELETE against a SQLite or SQL Server source, if the Masque policy lets the
        assistant write. Returns the number of rows changed, never data. Masque refuses an UPDATE that sets a masked
        column and any statement that copies a masked column into another column. CSV and Excel sources are
        read-only. Always confirm the exact statement with the user before calling this tool.
        """)]
    public CallToolResult Execute(
        [Description("Same as masque_describe's source; must be a SQLite or SQL Server database.")] string source,
        [Description("One INSERT, UPDATE or DELETE statement.")] string sql) =>
        ToResult(service.Execute(source, sql));

    private static CallToolResult ToResult(ToolReply reply) => new()
    {
        Content = [new TextContentBlock { Text = reply.Text }],
        IsError = reply.IsError,
    };
}
