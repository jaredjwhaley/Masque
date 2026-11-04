namespace Masque.Core.Detection;

/// <summary>
/// Decides each column's category from header and value votes. Strong votes beat weak ones; among the
/// remaining votes the most sensitive category wins; with no votes the column is masked as free text
/// (fail closed). Money and Code columns only pass through when their values back that up.
/// </summary>
/// <param name="headerDetector">Votes from header text.</param>
/// <param name="valueDetector">Votes from value shapes.</param>
public sealed class ColumnClassifier(HeaderDetector headerDetector, ValuePatternDetector valueDetector)
{
    /// <summary>Most sensitive first: when votes disagree, the earlier category wins.</summary>
    private static readonly PiiCategory[] SensitivityOrder =
    [
        PiiCategory.Ssn, PiiCategory.DateOfBirth, PiiCategory.Name, PiiCategory.Email, PiiCategory.Phone,
        PiiCategory.Address, PiiCategory.Identifier, PiiCategory.Date, PiiCategory.Zip, PiiCategory.FreeText,
        PiiCategory.Code, PiiCategory.Money, PiiCategory.Safe,
    ];

    /// <summary>Creates a classifier with the built-in detectors.</summary>
    /// <returns>A ready classifier.</returns>
    public static ColumnClassifier CreateDefault() => new(new HeaderDetector(), new ValuePatternDetector());

    /// <summary>Classifies one column.</summary>
    /// <param name="index">Zero-based column position.</param>
    /// <param name="header">The column header.</param>
    /// <param name="samples">A sample of the column's values.</param>
    /// <returns>The chosen category and the reason for it.</returns>
    public ColumnPlan Classify(int index, string header, IReadOnlyList<string?> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var votes = headerDetector.Detect(header)
            .Concat(valueDetector.Detect(samples))
            .Where(vote => ValuesSupport(vote.Category, samples))
            .ToList();

        var strong = votes.Where(v => v.Strength == VoteStrength.Strong).ToList();
        var pool = strong.Count > 0 ? strong : votes;

        if (pool.Count == 0)
        {
            return new ColumnPlan(index, header, PiiCategory.FreeText, "no header or value pattern matched; masked as free text");
        }

        var winner = pool.OrderBy(v => Array.IndexOf(SensitivityOrder, v.Category)).First();
        return new ColumnPlan(index, header, winner.Category, winner.Reason);
    }

    /// <summary>Pass-through categories need their values to agree; a blank column passes because there's nothing to leak.</summary>
    private static bool ValuesSupport(PiiCategory category, IReadOnlyList<string?> samples)
    {
        var allBlank = samples.All(string.IsNullOrWhiteSpace);

        return category switch
        {
            PiiCategory.Money => allBlank || ValuePatternDetector.LooksLikeMoney(samples),
            PiiCategory.Code => allBlank || ValuePatternDetector.LooksLikeCode(samples),
            _ => true,
        };
    }
}
