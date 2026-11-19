using Masque.Core;
using Masque.Core.Config;
using Masque.Core.Policy;
using Masque.Core.Query;
using Masque.Data.Dialects;
using Masque.Data.Review;

namespace Masque.Data.Masking;

/// <summary>
/// Decides how each result column is masked, from where the database says it came from, that table's reviewed
/// config and what the caller may see raw. Anything it can't place is masked (fail closed).
/// </summary>
/// <param name="reviewer">Loads reviewed configs for source tables.</param>
internal sealed class ColumnPlanner(TableReviewer reviewer)
{
    /// <summary>Plans every visible column of a result.</summary>
    /// <param name="columns">The result's columns, with lineage.</param>
    /// <param name="session">The connection the result came from.</param>
    /// <param name="grant">What the caller may see raw.</param>
    /// <param name="review">The statement's review, whose masked names cover computed copies of masked columns.</param>
    /// <returns>One plan per column, in order.</returns>
    /// <exception cref="ReviewRequiredException">A source table (one reached through a view, say) still needs review.</exception>
    public IReadOnlyList<ColumnMasking> Plan(IReadOnlyList<ResultColumn> columns, MasqueSession session, AccessGrant grant, StatementReview review)
    {
        var pending = new List<TableConfig>();
        var plans = columns.Select(column => PlanColumn(column, session, grant, review, pending)).ToList();
        return pending.Count > 0 ? throw new ReviewRequiredException(pending) : plans;
    }

    private ColumnMasking PlanColumn(ResultColumn column, MasqueSession session, AccessGrant grant, StatementReview review, List<TableConfig> pending)
    {
        if (!column.IsTraced)
        {
            return review.MaskedNames.TryGetValue(column.Name, out var maskedType)
                ? new ColumnMasking(column.Name, MaskAction.Tokenize, maskedType, "computed from a masked column")
                : new ColumnMasking(column.Name, MaskAction.CheckComputed, SensitiveDataType.FreeText,
                    "computed value; numbers pass, text is masked as free text");
        }

        var table = session.FindListed(column.Table!);
        if (table is null)
        {
            return new ColumnMasking(column.Name, MaskAction.Tokenize, SensitiveDataType.FreeText,
                "its source table isn't one the database lists; masked as free text");
        }

        var config = reviewer.Reviewed(session, table, pending);
        if (config is null)
        {
            return new ColumnMasking(column.Name, MaskAction.Tokenize, SensitiveDataType.FreeText, "source table awaits review");
        }

        // CategoryFor returns FreeText for a column missing from the config, so unknown columns fail closed too.
        var type = config.CategoryFor(column.Column!);
        var source = $"from {table.Qualified}.{column.Column}";

        if (!type.IsSensitive())
        {
            return new ColumnMasking(column.Name, MaskAction.CheckValues, type, $"{source}; reviewed as {type}, values checked");
        }

        return grant.Shows(type)
            ? new ColumnMasking(column.Name, MaskAction.PassThrough, type, $"{source}; {type} is shown raw by policy")
            : new ColumnMasking(column.Name, MaskAction.Tokenize, type, $"{source}; masked as {type}");
    }
}
