namespace Masque.Core.Detection;

/// <summary>
/// Decides what a column holds by asking every registered <see cref="ISensitiveDataDetector"/> and weighing their
/// votes: strong votes beat weak ones; among the rest the most sensitive type wins; with no votes the column is
/// masked as free text (fail closed). Pass-through types (money, codes) only win when their values back them up.
/// Immutable and thread-safe.
/// </summary>
public sealed class ColumnClassifier
{
    /// <summary>Most sensitive first: when votes disagree, the earlier type wins. Unlisted types rank last.</summary>
    private static readonly SensitiveDataType[] SensitivityOrder =
    [
        SensitiveDataType.Ssn, SensitiveDataType.DateOfBirth, SensitiveDataType.Name, SensitiveDataType.Email,
        SensitiveDataType.Phone, SensitiveDataType.Address, SensitiveDataType.Identifier, SensitiveDataType.Date,
        SensitiveDataType.Zip, SensitiveDataType.FreeText, SensitiveDataType.Code, SensitiveDataType.Money,
        SensitiveDataType.Safe,
    ];

    private readonly IReadOnlyList<ISensitiveDataDetector> _detectors;

    /// <summary>Creates a classifier over a set of detectors.</summary>
    /// <param name="detectors">The detectors to consult; several may share a type.</param>
    public ColumnClassifier(IEnumerable<ISensitiveDataDetector> detectors)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        _detectors = detectors.ToList();
    }

    /// <summary>A shared classifier with the built-in detectors.</summary>
    public static ColumnClassifier Default { get; } = CreateDefault();

    /// <summary>Creates a classifier with the built-in detectors.</summary>
    /// <returns>A ready classifier.</returns>
    public static ColumnClassifier CreateDefault() => new(DefaultDetectors());

    /// <summary>The built-in detectors, one per type.</summary>
    /// <returns>A new list of detector instances.</returns>
    public static IReadOnlyList<ISensitiveDataDetector> DefaultDetectors() =>
    [
        new SsnDetector(), new DateOfBirthDetector(), new NameDetector(), new EmailDetector(), new PhoneDetector(),
        new ZipDetector(), new AddressDetector(), new IdentifierDetector(), new DateDetector(), new MoneyDetector(),
        new CodeDetector(),
    ];

    /// <summary>Classifies one column.</summary>
    /// <param name="index">Zero-based column position.</param>
    /// <param name="header">The column header.</param>
    /// <param name="samples">A sample of the column's values.</param>
    /// <returns>The chosen type and the reason for it.</returns>
    public ColumnPlan Classify(int index, string header, IReadOnlyList<string?> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        header ??= string.Empty;
        var values = samples.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
        var votes = new List<ColumnVote>();

        foreach (var detector in _detectors)
        {
            // A blank column backs up any type: there is nothing in it to leak.
            if (values.Count > 0 && !detector.ValuesAllow(values))
            {
                continue;
            }

            AddIfPresent(votes, detector.CheckHeader(header));
            AddIfPresent(votes, values.Count > 0 ? detector.CheckValues(values) : null);
        }

        var strong = votes.Where(v => v.Strength == VoteStrength.Strong).ToList();
        var pool = strong.Count > 0 ? strong : votes;

        if (pool.Count == 0)
        {
            return new ColumnPlan(index, header, SensitiveDataType.FreeText, "no header or value pattern matched; masked as free text");
        }

        var winner = pool.OrderBy(v => Rank(v.Category)).First();
        return new ColumnPlan(index, header, winner.Category, winner.Reason);
    }

    /// <summary>Whether a column holds sensitive data: its classified type is anything but Safe, Money or Code.</summary>
    /// <param name="header">The column header.</param>
    /// <param name="samples">A sample of the column's values.</param>
    /// <returns><see langword="true"/> when the column must be masked by default, including when nothing is known.</returns>
    public bool ContainsSensitiveData(string header, IReadOnlyList<string?> samples) =>
        Classify(0, header, samples).Category.IsSensitive();

    /// <summary>Whether one value on its own has a distinctive sensitive shape, such as an SSN or an email address.</summary>
    /// <param name="value">One value; null and blank are never sensitive.</param>
    /// <returns><see langword="true"/> when any detector flags the value.</returns>
    public bool LooksLikeSensitiveValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && _detectors.Any(d => d.MatchesValue(value));

    private static void AddIfPresent(List<ColumnVote> votes, ColumnVote? vote)
    {
        if (vote is not null)
        {
            votes.Add(vote);
        }
    }

    private static int Rank(SensitiveDataType type)
    {
        var rank = Array.IndexOf(SensitivityOrder, type);
        return rank < 0 ? int.MaxValue : rank;
    }
}
