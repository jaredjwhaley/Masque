namespace Masque.Core.Detection;

/// <summary>Recognises street addresses and address parts smaller than a state by header words.</summary>
public sealed class AddressDetector() : SensitiveDataDetector(
    SensitiveDataType.Address,
    headerKeywords: ["address", "addr", "street", "city", "county"]);
