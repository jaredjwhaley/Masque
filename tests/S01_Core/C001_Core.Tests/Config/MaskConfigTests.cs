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
        var config = new MaskConfig("abc123", ReviewedByPerson: true,
            [new ColumnRule("SSN", SensitiveDataType.Ssn, "header"), new ColumnRule("Pay", SensitiveDataType.Money, "header")]);

        store.Save(config);

        var loaded = store.Load("abc123").ShouldNotBeNull();
        loaded.ReviewedByPerson.ShouldBeTrue();
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
        store.Save(new MaskConfig("abc123", ReviewedByPerson: false, [new ColumnRule("SSN", SensitiveDataType.Ssn, "header")]));

        File.ReadAllText(store.PathFor("abc123")).ShouldContain("\"Ssn\"");
    }

    [Fact]
    public void CategoryFor_MatchesHeadersLikeTheFingerprint_AndFailsClosed()
    {
        var config = new MaskConfig("x", ReviewedByPerson: true, [new ColumnRule("First Name", SensitiveDataType.Name, "header")]);

        config.CategoryFor("first_name").ShouldBe(SensitiveDataType.Name);
        config.CategoryFor("unknown").ShouldBe(SensitiveDataType.FreeText);
    }
}
