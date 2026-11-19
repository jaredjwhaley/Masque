using System.Text.RegularExpressions;

namespace Masque.Core.Detection;

/// <summary>
/// Recognises record, account, member and employee identifiers by header words. Plain numbers are only a weak hint,
/// used when nothing more specific votes, and are too common to flag one value at a time.
/// </summary>
public sealed partial class IdentifierDetector() : SensitiveDataDetector(
    SensitiveDataType.Identifier,
    headerKeywords: ["id", "ids", "identifier", "number", "num", "no", "acct", "account", "mrn"])
{
    /// <inheritdoc />
    public override ColumnVote? CheckValues(IReadOnlyList<string> values) =>
        VoteIfShared(values, NumberShape(), "numbers", VoteStrength.Weak);

    [GeneratedRegex(@"^-?[\d,]*\.?\d+$")]
    private static partial Regex NumberShape();
}
