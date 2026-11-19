using System.Text.RegularExpressions;

namespace Masque.Core.Detection;

/// <summary>Recognises US Social Security numbers by header words and the 123-45-6789 shape.</summary>
public sealed partial class SsnDetector() : SensitiveDataDetector(
    SensitiveDataType.Ssn,
    headerKeywords: ["ssn", "socialsecurity", "taxpayerid"],
    valueShape: Shape(),
    shapeDescription: "SSNs")
{
    [GeneratedRegex(@"^\d{3}-\d{2}-\d{4}$")]
    private static partial Regex Shape();
}
