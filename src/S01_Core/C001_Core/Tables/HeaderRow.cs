using Masque.Core.Detection;
using Masque.Core.Query;

namespace Masque.Core.Tables;

/// <summary>
/// Guards against files with no header row. Their "column names" are real values, and Masque shows column names
/// freely, so such a table is refused before its names are displayed or changed in any way.
/// </summary>
public static class HeaderRow
{
    /// <summary>Throws when any column name looks like personal data (an SSN, email, phone number or date).</summary>
    /// <param name="table">The table's name, for the message.</param>
    /// <param name="headers">The raw column names, before any renaming.</param>
    /// <exception cref="QueryRejectedException">The names look like a data row; the message doesn't repeat them.</exception>
    public static void EnsureLooksLikeHeaders(string table, IEnumerable<string?> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (headers.Any(ColumnClassifier.Default.LooksLikeSensitiveValue))
        {
            throw new QueryRejectedException(
                $"The first row of table {table} looks like data, not column names. Add a header row and try again.");
        }
    }
}
