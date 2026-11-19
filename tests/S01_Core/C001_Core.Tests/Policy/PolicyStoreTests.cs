using Masque.Core.Policy;

namespace Masque.Core.Tests.Policy;

/// <summary>
/// Pins down where policies come from: a JSON file, with an administrator's machine-wide file taking precedence over
/// the user's, a locked policy when neither exists, and a refusal (never a silent fallback) when a file is broken.
/// </summary>
public sealed class PolicyStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("masque-policy-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string name, string json)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>A policy file is read with its rules and default.</summary>
    [Fact]
    public void Load_ReadsRulesAndDefault()
    {
        var path = Write("policy.json", """
            {
              "default": { "rawTypes": ["Zip"], "canWrite": false },
              "rules": [ { "name": "hr", "groups": ["CONTOSO\\HR"], "rawTypes": ["Name"], "canWrite": true } ]
            }
            """);

        var policy = PolicyStore.Load(path).ShouldNotBeNull();

        policy.Default.RawTypes.ShouldBe([SensitiveDataType.Zip]);
        var rule = policy.Rules.ShouldHaveSingleItem();
        rule.Groups.ShouldBe(["CONTOSO\\HR"]);
        rule.Users.ShouldBeEmpty();
        rule.CanWrite.ShouldBeTrue();
    }

    /// <summary>No file means no policy from that place.</summary>
    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        PolicyStore.Load(Path.Combine(_folder, "none.json")).ShouldBeNull();
    }

    /// <summary>A broken file is refused, naming the file but quoting nothing from it.</summary>
    [Fact]
    public void Load_BrokenFile_Throws()
    {
        var path = Write("policy.json", "{ \"rules\": [ { \"rawTypes\": [\"NotAType\"] } ] }");

        var error = Should.Throw<PolicyException>(() => PolicyStore.Load(path));
        error.Message.ShouldContain(path);
        error.Message.ShouldNotContain("NotAType");
    }

    /// <summary>An administrator's machine-wide policy wins over the user's own.</summary>
    [Fact]
    public void LoadEffective_MachineFileWins()
    {
        var machine = Write("machine.json", """{ "default": { "rawTypes": ["Zip"] } }""");
        var user = Write("user.json", """{ "default": { "rawTypes": ["Name"], "canWrite": true } }""");

        var policy = PolicyStore.LoadEffective(machine, user);

        policy.Default.RawTypes.ShouldBe([SensitiveDataType.Zip]);
        policy.Default.CanWrite.ShouldBeFalse();
    }

    /// <summary>Without any policy file, everything sensitive is masked and nothing may be written.</summary>
    [Fact]
    public void LoadEffective_NoFiles_IsLocked()
    {
        PolicyStore.LoadEffective(Path.Combine(_folder, "a.json"), Path.Combine(_folder, "b.json")).ShouldBe(MaskingPolicy.Locked);
    }
}
