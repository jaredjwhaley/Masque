using Masque.Core;

namespace Masque.Cli.Tests;

public class CliOptionsTests
{
    [Fact]
    public void TryParse_FileOnly_UsesDefaults()
    {
        CliOptions.TryParse(["data.csv"], out var options, out _).ShouldBeTrue();

        options!.InputPath.ShouldBe("data.csv");
        options.OutputPath.ShouldBeNull();
        options.Accept.ShouldBeFalse();
        options.VaultPath.ShouldBe(MasquePaths.DefaultVault);
        options.ConfigFolder.ShouldBe(MasquePaths.DefaultConfigs);
    }

    [Fact]
    public void TryParse_AllOptions_AreRead()
    {
        CliOptions.TryParse(["data.csv", "--out", "o.csv", "--accept", "--vault", "v.db", "--configs", "cfg"], out var options, out _)
            .ShouldBeTrue();

        options!.OutputPath.ShouldBe("o.csv");
        options.Accept.ShouldBeTrue();
        options.VaultPath.ShouldBe("v.db");
        options.ConfigFolder.ShouldBe("cfg");
    }

    [Theory]
    [InlineData]
    [InlineData("--accept")]
    [InlineData("a.csv", "b.csv")]
    [InlineData("a.csv", "--out")]
    [InlineData("a.csv", "--bogus")]
    public void TryParse_BadArguments_ExplainWhy(params string[] args)
    {
        CliOptions.TryParse(args, out var options, out var error).ShouldBeFalse();

        options.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("data.csv", "--help")]
    public void IsHelp_RecognizesHelpFlags(params string[] args)
    {
        CliOptions.IsHelp(args).ShouldBeTrue();
    }

    [Fact]
    public void Usage_DescribesTheCommand()
    {
        CliOptions.Usage.ShouldContain("Usage: masque");
    }

    [Fact]
    public void DefaultOutputPath_AddsMasqueBeforeTheExtension()
    {
        CliOptions.DefaultOutputPath(Path.Combine("folder", "data.csv")).ShouldBe(Path.Combine("folder", "data.masque.csv"));
    }
}
