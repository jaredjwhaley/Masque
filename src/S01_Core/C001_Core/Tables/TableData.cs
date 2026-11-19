namespace Masque.Core.Tables;

/// <summary>
/// A table read from a file or sampled from a database: a header row plus data rows, every cell as text.
/// It holds real values, so it never leaves the local process; only masked results are shown to anyone.
/// </summary>
/// <param name="Name">The table's name as queries refer to it.</param>
/// <param name="Headers">Column names, in column order.</param>
/// <param name="Rows">Data rows. A row may be shorter than the header; missing cells count as null.</param>
public sealed record TableData(string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string?>> Rows)
{
    /// <summary>Turns the first rows into one list of values per column, the shape column detection needs.</summary>
    /// <param name="maxRows">Most rows to sample.</param>
    /// <returns>For each header, its values from the sampled rows (null where a row is short).</returns>
    public IReadOnlyList<IReadOnlyList<string?>> ColumnSamples(int maxRows)
    {
        var sampled = Rows.Take(Math.Max(0, maxRows)).ToList();

        return Headers
            .Select((_, column) => (IReadOnlyList<string?>)sampled.Select(row => column < row.Count ? row[column] : null).ToList())
            .ToList();
    }
}
