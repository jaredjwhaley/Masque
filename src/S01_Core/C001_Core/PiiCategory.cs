namespace Masque.Core;

/// <summary>
/// What kind of value a column or text span holds. The category picks the rule that de-identifies it.
/// </summary>
public enum PiiCategory
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
