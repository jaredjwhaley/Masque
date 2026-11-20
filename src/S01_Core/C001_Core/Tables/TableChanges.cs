namespace Masque.Core.Tables;

/// <summary>One cell whose value a write changed.</summary>
/// <param name="Row">The row's position in the table's data rows (0 is the first row under the header).</param>
/// <param name="Column">The column's position in the header.</param>
/// <param name="Value">The new value: a <see cref="string"/>, <see cref="long"/>, <see cref="double"/>, or null for a blank cell.</param>
public sealed record CellChange(int Row, int Column, object? Value);

/// <summary>
/// What a write did to one table loaded from a file, so a file writer can apply the same change to the file.
/// Row positions refer to the table as it was read, before any change.
/// </summary>
/// <param name="Updates">Cells whose value changed, in row then column order.</param>
/// <param name="DeletedRows">Positions of rows that were deleted, in ascending order.</param>
/// <param name="InsertedRows">New rows to add after the existing ones, one value per header column, typed as in <see cref="CellChange.Value"/>.</param>
public sealed record TableChanges(
    IReadOnlyList<CellChange> Updates,
    IReadOnlyList<int> DeletedRows,
    IReadOnlyList<IReadOnlyList<object?>> InsertedRows)
{
    /// <summary>A table nothing changed.</summary>
    public static TableChanges None { get; } = new([], [], []);

    /// <summary>Whether the write left the table as it was.</summary>
    public bool IsEmpty => Updates.Count == 0 && DeletedRows.Count == 0 && InsertedRows.Count == 0;
}
