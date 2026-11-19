using System.Text.RegularExpressions;

namespace Masque.Core.Detection;

/// <summary>Recognises US ZIP codes by header words and the ZIP+4 shape. A single ZIP is too common a shape to flag alone.</summary>
public sealed partial class ZipDetector() : SensitiveDataDetector(
    SensitiveDataType.Zip,
    headerKeywords: ["zip", "postal", "postcode"],
    valueShape: Shape(),
    shapeDescription: "ZIP+4 codes",
    shapeFlagsSingleValues: false)
{
    [GeneratedRegex(@"^\d{5}-\d{4}$")]
    private static partial Regex Shape();
}
