using Masque.Core.Config;

namespace Masque.Core.Tests.Config;

public sealed class MaskConfigTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "masque-tests", $"{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Fingerprint_IgnoresColumnOrderCaseAndPunctuation()
    {
        SchemaFingerprint.Compute(["First Name", "SSN"])
            .ShouldBe(SchemaFingerprint.Compute(["ssn", "first_name"]));
    }

    [Fact]
    public void Fingerprint_DifferentHeaders_Differ()
    {
        SchemaFingerprint.Compute(["SSN", "Name"]).ShouldNotBe(SchemaFingerprint.Compute(["SSN", "Email"]));
    }

    [Fact]
    public void Fingerprint_IsShortHex()
    {
        SchemaFingerprint.Compute(["SSN"]).ShouldMatch("^[0-9a-f]{16}$");
    }

    [Fact]
    public void Store_SaveThenLoad_RoundTrips()
    {
        var store = new ConfigStore(_folder);
        var config = new MaskConfig("abc123", Confirmed: true,
            [new ColumnRule("SSN", PiiCategory.Ssn, "header"), new ColumnRule("Pay", PiiCategory.Money, "header")]);

        store.Save(config);

        var loaded = store.Load("abc123").ShouldNotBeNull();
        loaded.Confirmed.ShouldBeTrue();
        loaded.Columns.ShouldBe(config.Columns);
    }

    [Fact]
    public void Store_MissingConfig_ReturnsNull()
    {
        new ConfigStore(_folder).Load("nothing").ShouldBeNull();
    }

    [Fact]
    public void Store_WritesCategoriesByName_SoPeopleCanEditThem()
    {
        var store = new ConfigStore(_folder);
        store.Save(new MaskConfig("abc123", Confirmed: false, [new ColumnRule("SSN", PiiCategory.Ssn, "header")]));

        File.ReadAllText(store.PathFor("abc123")).ShouldContain("\"Ssn\"");
    }

    [Fact]
    public void CategoryFor_MatchesHeadersLikeTheFingerprint_AndFailsClosed()
    {
        var config = new MaskConfig("x", Confirmed: true, [new ColumnRule("First Name", PiiCategory.Name, "header")]);

        config.CategoryFor("first_name").ShouldBe(PiiCategory.Name);
        config.CategoryFor("unknown").ShouldBe(PiiCategory.FreeText);
    }
}
