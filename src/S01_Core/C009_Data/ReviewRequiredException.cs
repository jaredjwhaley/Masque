using Masque.Core.Config;

namespace Masque.Data;

/// <summary>
/// A query touches tables whose column settings no person has reviewed yet. Masque saved a proposal for each; a
/// person checks the file, sets <c>reviewedByPerson</c> to true, and runs the query again. The message names tables
/// and files only, never values.
/// </summary>
public sealed class ReviewRequiredException : Exception
{
    /// <summary>Creates the exception for the tables waiting on review.</summary>
    /// <param name="pending">Each table's proposed config and where it is saved.</param>
    public ReviewRequiredException(IReadOnlyList<TableConfig> pending)
        : base(Describe(pending))
    {
        Pending = pending;
    }

    /// <summary>The tables waiting on review, with their proposed configs and file paths.</summary>
    public IReadOnlyList<TableConfig> Pending { get; }

    private static string Describe(IReadOnlyList<TableConfig> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var tables = string.Join("; ", pending.Select(p => $"{p.Table} ({p.ConfigPath})"));
        return $"A person must review the column settings for: {tables}. Check each column's type in the file, set "
            + "reviewedByPerson to true, then run the query again.";
    }
}
