using System.Text.Json.Nodes;
using Masque.Core;
using Masque.Core.Config;
using Masque.Vault.Tests.TestSupport;

namespace Masque.Cli.Tests;

public sealed class MasqueCliTests : IDisposable
{
    // Synthetic values only. Each is distinctive so a leak is easy to spot.
    private const string Ssn = "123-45-6789";
    private const string FirstName = "Zebulon";
    private const string Email = "zebulon.quark@example.com";
    private const string Note = "allergic to penicillin";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "masque-tests", $"{Guid.NewGuid():N}");
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly FakeKeyProtector _protector = new("tester");

    public MasqueCliTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string VaultPath => Path.Combine(_folder, "vault.db");

    private string ConfigFolder => Path.Combine(_folder, "configs");

    private string WriteInput(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string StandardInput(string name = "people.csv") => WriteInput(name,
        "SSN,FirstName,Email,GrossPay,Status,Notes\n" +
        $"{Ssn},{FirstName},{Email},\"1,234.56\",A,{Note}\n" +
        "987-65-4321,Lee,lee@example.org,980.00,T,\n");

    private int Run(params string[] args) =>
        MasqueCli.Run([.. args, "--vault", VaultPath, "--configs", ConfigFolder], _out, _err, _protector);

    private string AllConsoleText => _out.ToString() + _err.ToString();

    [Fact]
    public void FirstRun_WithoutAccept_ProposesAConfigAndWritesNothing()
    {
        var input = StandardInput();

        Run(input).ShouldBe(ExitCodes.NeedsReview);

        File.Exists(CliOptions.DefaultOutputPath(input)).ShouldBeFalse();
        var config = new ConfigStore(ConfigFolder).Load(SchemaFingerprint.Compute(["SSN", "FirstName", "Email", "GrossPay", "Status", "Notes"]));
        config.ShouldNotBeNull().Confirmed.ShouldBeFalse();
        _out.ToString().ShouldContain("SSN");
        _out.ToString().ShouldContain("--accept");
    }

    [Fact]
    public void Accept_WritesAMaskedCopyWithNoRealValues()
    {
        var input = StandardInput();

        Run(input, "--accept").ShouldBe(ExitCodes.Success);

        var output = File.ReadAllText(CliOptions.DefaultOutputPath(input));
        foreach (var value in new[] { Ssn, FirstName, Email, Note })
        {
            output.ShouldNotContain(value);
        }

        output.ShouldContain("\"1,234.56\"");
        output.ShouldContain("SSN,FirstName,Email,GrossPay,Status,Notes");
    }

    [Fact]
    public void ConfirmedShape_RunsWithoutAcceptNextTime()
    {
        Run(StandardInput("one.csv"), "--accept");

        Run(StandardInput("two.csv")).ShouldBe(ExitCodes.Success);
    }

    [Fact]
    public void SameValueInTwoFiles_GetsTheSameToken()
    {
        var first = StandardInput("one.csv");
        var second = WriteInput("other-shape.csv", $"Social Security Number,Dept\n{Ssn},Ops\n");

        Run(first, "--accept");
        Run(second, "--accept");

        var tokenInFirst = File.ReadAllLines(CliOptions.DefaultOutputPath(first))[1].Split(',')[0];
        var tokenInSecond = File.ReadAllLines(CliOptions.DefaultOutputPath(second))[1].Split(',')[0];
        tokenInSecond.ShouldBe(tokenInFirst);
    }

    [Fact]
    public void Console_NeverShowsAValue()
    {
        var input = StandardInput();

        Run(input);
        Run(input, "--accept");

        foreach (var value in new[] { Ssn, FirstName, Email, Note })
        {
            AllConsoleText.ShouldNotContain(value);
        }
    }

    [Fact]
    public void EditedConfig_IsUsedOnAccept()
    {
        var input = WriteInput("edit.csv", "Notes\nkeep me\n");
        Run(input);

        // A person reviews the proposed config and marks the column safe.
        var store = new ConfigStore(ConfigFolder);
        var path = store.PathFor(SchemaFingerprint.Compute(["Notes"]));
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["columns"]![0]!["category"] = nameof(PiiCategory.Safe);
        File.WriteAllText(path, json.ToJsonString());

        Run(input, "--accept").ShouldBe(ExitCodes.Success);

        File.ReadAllText(CliOptions.DefaultOutputPath(input)).ShouldContain("keep me");
    }

    [Fact]
    public void FirstRowLooksLikeData_RefusesWithoutEchoingIt()
    {
        var input = WriteInput("noheader.csv", $"{Ssn},{Email}\n987-65-4321,lee@example.org\n");

        Run(input, "--accept").ShouldBe(ExitCodes.Error);

        _err.ToString().ShouldContain("header");
        AllConsoleText.ShouldNotContain(Ssn);
        AllConsoleText.ShouldNotContain(Email);
    }

    [Fact]
    public void MissingFile_IsAnError()
    {
        Run(Path.Combine(_folder, "nope.csv")).ShouldBe(ExitCodes.Error);
    }

    [Fact]
    public void BadArguments_AreAUsageError()
    {
        MasqueCli.Run(["--bogus"], _out, _err, _protector).ShouldBe(ExitCodes.Usage);
        _err.ToString().ShouldContain("Usage: masque");
    }

    [Fact]
    public void Help_PrintsUsageAndSucceeds()
    {
        MasqueCli.Run(["--help"], _out, _err, _protector).ShouldBe(ExitCodes.Success);
        _out.ToString().ShouldContain("Usage: masque");
    }
}
