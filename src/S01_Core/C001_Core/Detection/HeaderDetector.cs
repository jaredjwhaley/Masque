namespace Masque.Core.Detection;

/// <summary>
/// Votes on a column's category from its header text alone. Keywords of 5 or more letters match anywhere in the
/// normalized header ("telephone" contains "phone"); shorter ones must be a whole word ("SSN", "Zip"), so "id"
/// doesn't match inside "paid".
/// </summary>
public sealed class HeaderDetector
{
    private const int SubstringKeywordLength = 5;

    private static readonly HeaderRule[] Rules =
    [
        new(PiiCategory.Ssn, ["ssn", "socialsecurity", "taxpayerid"]),
        new(PiiCategory.DateOfBirth, ["dob", "birth"]),
        new(PiiCategory.Name, ["name", "firstname", "lastname", "middlename", "fullname", "surname", "fname", "lname"],
            Exclusions: ["company", "employer", "business", "file", "plan", "product", "department", "dept", "client", "vendor"]),
        new(PiiCategory.Email, ["email", "mail"]),
        new(PiiCategory.Phone, ["phone", "mobile", "cell", "fax", "tel"]),
        new(PiiCategory.Zip, ["zip", "postal", "postcode"]),
        new(PiiCategory.Address, ["address", "addr", "street", "city", "county"]),
        new(PiiCategory.Identifier, ["id", "ids", "identifier", "number", "num", "no", "acct", "account", "mrn"]),
        new(PiiCategory.Date, ["date", "dt"]),
        new(PiiCategory.Money, ["amount", "amt", "pay", "wage", "wages", "salary", "rate", "hours", "hrs", "gross", "net", "tax",
            "deduction", "premium", "balance", "total", "ytd", "earning"]),
        new(PiiCategory.Code, ["code", "status", "type", "frequency", "freq", "state", "gender", "sex", "flag", "category"]),
    ];

    /// <summary>Returns a strong vote for every category whose keywords appear in the header.</summary>
    /// <param name="header">The column header. Null or empty gives no votes.</param>
    /// <returns>Zero or more votes.</returns>
    public IReadOnlyList<ColumnVote> Detect(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return [];
        }

        var normalized = HeaderText.Normalize(header);
        var words = HeaderText.Words(header);

        return Rules
            .Where(rule => rule.Keywords.Any(k => Matches(k, normalized, words))
                && !rule.Exclusions.Any(k => Matches(k, normalized, words)))
            .Select(rule => new ColumnVote(rule.Category, VoteStrength.Strong, $"header \"{header}\" suggests {rule.Category}"))
            .ToList();
    }

    private static bool Matches(string keyword, string normalized, IReadOnlyList<string> words) =>
        keyword.Length >= SubstringKeywordLength ? normalized.Contains(keyword, StringComparison.Ordinal) : words.Contains(keyword);

    /// <summary>Keywords that vote for a category, and words that cancel that vote.</summary>
    private sealed record HeaderRule(PiiCategory Category, string[] Keywords, string[]? Exclusions = null)
    {
        public string[] Exclusions { get; } = Exclusions ?? [];
    }
}
