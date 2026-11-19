namespace Masque.Core;

/// <summary>
/// The kinds of data Masque recognises. A column's type picks the rule that de-identifies its values, and policies
/// name types to say what a caller may see raw.
/// </summary>
public enum SensitiveDataType
{
    /// <summary>Not sensitive; passed through unchanged. Only assigned when a person or saved config says so.</summary>
    Safe,

    /// <summary>A person's name.</summary>
    Name,

    /// <summary>US Social Security number.</summary>
    Ssn,

    /// <summary>Date of birth.</summary>
    DateOfBirth,

    /// <summary>Any other date tied to a person (hire, admission, service).</summary>
    Date,

    /// <summary>Street address or any address part smaller than a state.</summary>
    Address,

    /// <summary>US ZIP code.</summary>
    Zip,

    /// <summary>Email address.</summary>
    Email,

    /// <summary>Phone or fax number.</summary>
    Phone,

    /// <summary>Record, account, member or employee identifier.</summary>
    Identifier,

    /// <summary>Monetary amount, hours or rate; passed through so calculations still reproduce.</summary>
    Money,

    /// <summary>Non-identifying code such as a status or pay-frequency code; passed through.</summary>
    Code,

    /// <summary>Free text that may contain anything, such as a notes field.</summary>
    FreeText,
}

/// <summary>Helpers for <see cref="SensitiveDataType"/>.</summary>
public static class SensitiveDataTypes
{
    /// <summary>
    /// Whether values of this type identify or describe a person and so are masked unless a policy allows them raw.
    /// Only <see cref="SensitiveDataType.Safe"/>, <see cref="SensitiveDataType.Money"/> and
    /// <see cref="SensitiveDataType.Code"/> are not.
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns><see langword="true"/> for every type that is masked by default.</returns>
    public static bool IsSensitive(this SensitiveDataType type) =>
        type is not (SensitiveDataType.Safe or SensitiveDataType.Money or SensitiveDataType.Code);
}
