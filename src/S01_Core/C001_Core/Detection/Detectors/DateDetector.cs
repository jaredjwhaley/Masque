using System.Text.RegularExpressions;

namespace Masque.Core.Detection;

/// <summary>Recognises dates tied to a person (hire, admission, service) by header words and value shape.</summary>
public sealed partial class DateDetector() : SensitiveDataDetector(
    SensitiveDataType.Date,
    headerKeywords: ["date", "dt"],
    valueShape: Shape(),
    shapeDescription: "dates")
{
    [GeneratedRegex(@"^(\d{4}-\d{1,2}-\d{1,2}|\d{1,2}/\d{1,2}/\d{2,4})([ T]\d{1,2}:\d{2}(:\d{2})?.*)?$")]
    private static partial Regex Shape();
}
