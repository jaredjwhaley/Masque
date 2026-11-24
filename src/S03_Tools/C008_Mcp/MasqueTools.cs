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
    /// <param name="source">A named source, a database, or a CSV or Excel file or folder.</param>
    /// <param name="sql">One INSERT, UPDATE or DELETE statement.</param>
    /// <param name="saveAs">Optional new file name for a CSV or Excel source; the source is then left unchanged.</param>
    /// <returns>The tool result.</returns>
    [McpServerTool(Name = "masque_execute", Title = "Change data through Masque", ReadOnly = false, Destructive = true,
        Idempotent = false, OpenWorld = false)]
    [Description("""
        Runs one INSERT, UPDATE or DELETE against a SQLite, SQL Server, CSV or Excel source, if the Masque policy lets
        the assistant write. Returns the number of rows changed and the files saved, never data.
        A masked column may be set to a value you supply (a literal or NULL), such as
        UPDATE Sheet1 SET PayDate = '2026-11-27'; write dates as yyyy-MM-dd and Excel stores them as dates in the
        column's format. A token you were shown, written into a masked column of its type, is saved as the real value
        it stands for. WHERE filters work as in masque_query, tokens included. Copying a masked column into another
        column, expressions and INSERT ... SELECT on masked columns are refused.
        CSV and Excel: by default the file is replaced and its previous version kept beside it as a backup. Pass saveAs
        to write the result to a new file in the same folder instead (it must not exist and keeps the source's
        extension); the source file is not changed. Excel saves need .xlsx and refuse formula cells, charts and
        pivot tables.
        Always confirm the exact statement and file with the user before calling this tool.
        """)]
    public CallToolResult Execute(
        [Description("Same as masque_describe's source.")] string source,
        [Description("One INSERT, UPDATE or DELETE statement.")] string sql,
        [Description("Optional, for a single CSV or Excel file: the new file's name (for example \"export-2026-11-27.xlsx\") or its full path in the source's folder. Leave out to save over the source with a backup.")]
        string? saveAs = null) =>
        ToResult(service.Execute(source, sql, saveAs));

    private static CallToolResult ToResult(ToolReply reply) => new()
    {
        Content = [new TextContentBlock { Text = reply.Text }],
        IsError = reply.IsError,
    };
}
