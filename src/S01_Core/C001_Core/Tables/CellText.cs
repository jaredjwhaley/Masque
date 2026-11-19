using System.Globalization;

namespace Masque.Core.Tables;

/// <summary>
/// Turns a database or spreadsheet value into the text Masque detects and tokenizes. Culture-independent, so
/// the same value always gives the same text (and therefore the same token) on any machine.
/// </summary>
public static class CellText
{
    /// <summary>Shown instead of binary values, which are never passed on.</summary>
    public const string BinaryPlaceholder = "[binary]";

    /// <summary>Converts one value.</summary>
    /// <param name="value">A value from a data reader or spreadsheet; null and <see cref="DBNull"/> mean no value.</param>
    /// <returns>The text form, or <see langword="null"/> for no value.</returns>
    public static string? From(object? value) => value switch
    {
        null or DBNull => null,
        string text => text,
        byte[] => BinaryPlaceholder,

        // ISO dates match the date detector's pattern; the time is added only when there is one.
        DateTime date when date.TimeOfDay == TimeSpan.Zero => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
