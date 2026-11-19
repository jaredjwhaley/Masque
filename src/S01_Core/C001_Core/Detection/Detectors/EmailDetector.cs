using System.Text.RegularExpressions;

namespace Masque.Core.Detection;

/// <summary>Recognises email addresses by header words and value shape.</summary>
public sealed partial class EmailDetector() : SensitiveDataDetector(
    SensitiveDataType.Email,
    headerKeywords: ["email", "mail"],
    valueShape: Shape(),
    shapeDescription: "email addresses")
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Shape();
}
