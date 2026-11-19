namespace Masque.Core.Tests;

/// <summary>Pins down where Masque keeps its files, so every tool shares one vault and one set of configs.</summary>
public sealed class MasquePathsTests
{
    /// <summary>Without an override, Masque lives under local app data.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void ResolveRoot_NoOverride_UsesLocalAppData(string? value) =>
        MasquePaths.ResolveRoot(value).ShouldBe(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Masque"));

    /// <summary>The MASQUE_HOME override wins and is made a full path.</summary>
    [Fact]
    public void ResolveRoot_Override_IsUsedAsFullPath()
    {
        var folder = Path.Combine(Path.GetTempPath(), "masque-home");

        MasquePaths.ResolveRoot(folder).ShouldBe(Path.GetFullPath(folder));
    }
}
