namespace Masque.Core.Detection;

/// <summary>Recognises dates of birth by header words; their values look like any other date.</summary>
public sealed class DateOfBirthDetector() : SensitiveDataDetector(
    SensitiveDataType.DateOfBirth,
    headerKeywords: ["dob", "birth"]);
