namespace Masque.Core.Detection;

/// <summary>Recognises person names by header words; organisation and product names are excluded.</summary>
public sealed class NameDetector() : SensitiveDataDetector(
    SensitiveDataType.Name,
    headerKeywords: ["name", "firstname", "lastname", "middlename", "fullname", "surname", "fname", "lname"],
    headerExclusions: ["company", "employer", "business", "file", "plan", "product", "department", "dept", "client", "vendor"]);
