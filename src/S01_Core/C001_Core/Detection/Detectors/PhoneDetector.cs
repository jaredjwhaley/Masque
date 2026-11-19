using System.Text.RegularExpressions;

namespace Masque.Core.Detection;

/// <summary>Recognises phone and fax numbers by header words and value shape.</summary>
public sealed partial class PhoneDetector() : SensitiveDataDetector(
    SensitiveDataType.Phone,
    headerKeywords: ["phone", "mobile", "cell", "fax", "tel"],
    valueShape: Shape(),
    shapeDescription: "phone numbers")
{
    // Needs a separator before the last 4 digits, so a bare 10-digit number is not mistaken for a phone.
    [GeneratedRegex(@"^(\+?1[-. ]?)?\(?\d{3}\)?[-. ]?\d{3}[-. ]\d{4}$")]
    private static partial Regex Shape();
}
