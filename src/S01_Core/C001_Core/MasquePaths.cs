namespace Masque.Core;

/// <summary>
/// Where Masque keeps its files by default. Shared by every Masque tool, so the CLI and the query server use the
/// same vault (and therefore the same tokens) and the same configs.
/// </summary>
/// <remarks>
/// The folder is the <see cref="HomeVariable"/> environment variable when set, otherwise "Masque" under the
/// user's local app data folder. Set the variable when tools run inside packaged apps (such as Claude Desktop on
/// Windows), which silently redirect local app data to a private folder and would otherwise get their own vault.
/// </remarks>
public static class MasquePaths
{
    /// <summary>Environment variable that overrides the Masque folder.</summary>
    public const string HomeVariable = "MASQUE_HOME";

    /// <summary>The per-user Masque folder.</summary>
    public static readonly string Root = ResolveRoot(Environment.GetEnvironmentVariable(HomeVariable));

    /// <summary>Default vault file.</summary>
    public static readonly string DefaultVault = Path.Combine(Root, "vault.db");

    /// <summary>Default folder of saved column configs.</summary>
    public static readonly string DefaultConfigs = Path.Combine(Root, "configs");

    /// <summary>Default file of named database sources (connection strings stay here, never in a tool call).</summary>
    public static readonly string DefaultSources = Path.Combine(Root, "sources.json");

    /// <summary>The user's own access policy (which types they see raw, whether they may write).</summary>
    public static readonly string DefaultPolicy = Path.Combine(Root, "policy.json");

    /// <summary>
    /// An administrator's machine-wide policy (<c>%ProgramData%\Masque\policy.json</c> on Windows). When it exists it
    /// replaces the user's policy, so a workplace can set the rules centrally.
    /// </summary>
    public static readonly string MachinePolicy =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Masque", "policy.json");

    /// <summary>Picks the Masque folder from the override, or local app data when the override is blank.</summary>
    /// <param name="overrideFolder">The <see cref="HomeVariable"/> value, or null.</param>
    /// <returns>A full path.</returns>
    public static string ResolveRoot(string? overrideFolder) =>
        string.IsNullOrWhiteSpace(overrideFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Masque")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideFolder.Trim()));
}
