using System.Globalization;
using System.Text;
using Masque.Core;
using Masque.Core.Config;
using Masque.Data.Masking;

namespace Masque.Mcp;

/// <summary>
/// Writes tool replies as plain text and Markdown tables, which assistants read well. Only masked rows,
/// headers, categories, reasons, names, paths and counts are ever written.
/// </summary>
public static class ReplyFormatter
{
    /// <summary>Describes a source's tables and their column categories.</summary>
    /// <param name="source">The source's display name.</param>
    /// <param name="tables">Each table's config, reviewed or proposed.</param>
    /// <returns>The reply text.</returns>
    public static string Describe(string source, IReadOnlyList<TableConfig> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Source {source}: {Count(tables.Count, "table")}.");
        text.AppendLine("Query them with masque_query, using these table and column names.");

        foreach (var table in tables)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"{table.Table} ({(table.Config.ReviewedByPerson ? "reviewed" : "needs review")})");
            AppendColumns(text, table.Config);
        }

        var pending = tables.Where(t => !t.Config.ReviewedByPerson).ToList();
        if (pending.Count > 0)
        {
            text.AppendLine();
            AppendReviewSteps(text, pending);
        }

        return text.ToString();
    }

    /// <summary>Explains that tables need review before a query can return rows.</summary>
    /// <param name="tables">The tables needing review.</param>
    /// <returns>The reply text.</returns>
    public static string NeedsReview(IReadOnlyList<TableConfig> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var text = new StringBuilder();
        text.AppendLine("No rows returned: Masque has not yet been told which columns are sensitive in these tables.");
        foreach (var table in tables)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"{table.Table} (proposed)");
            AppendColumns(text, table.Config);
        }

        text.AppendLine();
        AppendReviewSteps(text, tables);
        return text.ToString();
    }

    /// <summary>Writes a masked result as a column legend and a Markdown table.</summary>
    /// <param name="source">The source's display name.</param>
    /// <param name="result">The masked result.</param>
    /// <returns>The reply text.</returns>
    public static string Result(string source, QueryTable result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{source}: {Count(result.Rows.Count, "row")}");
        text.AppendLine(result.Truncated
            ? $" (more rows exist; narrow the query or raise maxRows, up to {MasqueToolService.MaxRowsLimit})."
            : ".");

        if (result.Columns.Count == 0)
        {
            return text.ToString();
        }

        text.AppendLine("Masked values are consistent tokens: the same real value always gets the same token, so equal tokens mean equal values.");
        foreach (var column in result.Columns)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {column.Name}: {column.Type}, {Treatment(column.Action)} ({column.Reason})");
        }

        text.AppendLine();
        text.AppendLine(Row(result.Columns.Select(c => c.Name)));
        text.AppendLine(Row(result.Columns.Select(_ => "---")));
        foreach (var row in result.Rows)
        {
            text.AppendLine(Row(row));
        }

        return text.ToString();
    }

    /// <summary>Reports a write, and each file it was saved to with that file's backup. Names files, never data.</summary>
    /// <param name="source">The source's display name.</param>
    /// <param name="rowsChanged">The number of rows the statement changed.</param>
    /// <param name="saved">CSV or Excel files the write was saved to; empty for databases.</param>
    /// <returns>The reply text.</returns>
    public static string Executed(string source, int rowsChanged, IReadOnlyList<SavedFile> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);

        var text = new StringBuilder(
            string.Create(CultureInfo.InvariantCulture, $"{source}: {Count(Math.Max(rowsChanged, 0), "row")} changed."));
        foreach (var file in saved)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" Saved {file.Path}; its previous version is in {Path.GetFileName(file.BackupPath)} in the same folder.");
        }

        return text.ToString();
    }

    /// <summary>Says in words what Masque did with a column's values.</summary>
    private static string Treatment(MaskAction action) => action switch
    {
        MaskAction.PassThrough => "real values (allowed raw)",
        MaskAction.Tokenize => "masked",
        MaskAction.CheckValues => "real values, any value that looks sensitive is masked",
        _ => "computed: numbers shown, text masked",
    };

    private static void AppendColumns(StringBuilder text, MaskConfig config)
    {
        var width = config.Columns.Count == 0 ? 0 : config.Columns.Max(c => c.Header.Length);
        foreach (var column in config.Columns)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {column.Header.PadRight(width)}  {column.Category,-11}  {column.Reason}");
        }
    }

    private static void AppendReviewSteps(StringBuilder text, IEnumerable<TableConfig> tables)
    {
        text.AppendLine("A person must review these before Masque returns rows. Ask the user to open each file below,");
        text.AppendLine($"check every column's category ({string.Join(", ", Enum.GetNames<SensitiveDataType>())}),");
        text.AppendLine("change any that are wrong, set \"reviewedByPerson\": true and save. Then run the query again.");
        text.AppendLine("Do not edit or confirm these files yourself: the review is the user's decision.");
        foreach (var table in tables)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {table.Table}: {table.ConfigPath}");
        }
    }

    /// <summary>One Markdown table row; pipes and line breaks inside cells are escaped so the table stays intact.</summary>
    private static string Row(IEnumerable<string?> cells) =>
        "| " + string.Join(" | ", cells.Select(c => (c ?? string.Empty)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal))) + " |";

    private static string Count(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");
}
