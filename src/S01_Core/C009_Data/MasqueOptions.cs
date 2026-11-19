using Masque.Core;
using Masque.Core.Detection;
using Masque.Core.Policy;
using Masque.Core.Vault;
using Masque.Data.Dialects;
using Masque.Vault;

namespace Masque.Data;

/// <summary>
/// Settings for building a <see cref="MasqueGate"/>, for <see cref="MasqueGate.Create"/> or <c>services.AddMasque(...)</c>.
/// Everything has a safe default except <see cref="Dialects"/>, which names the databases the application uses.
/// </summary>
public sealed class MasqueOptions
{
    /// <summary>
    /// A policy set in code. Precedence, highest first: the machine policy file (an administrator's), this policy,
    /// the user's policy file, then <see cref="MaskingPolicy.Locked"/> (nothing raw, no writes).
    /// </summary>
    public MaskingPolicy? Policy { get; set; }

    /// <summary>The machine-wide policy file an administrator controls. Defaults to <see cref="MasquePaths.MachinePolicy"/>.</summary>
    public string? MachinePolicyPath { get; set; }

    /// <summary>The user's own policy file. Defaults to <see cref="MasquePaths.DefaultPolicy"/>.</summary>
    public string? PolicyPath { get; set; }

    /// <summary>Says who is calling. Defaults to the signed-in Windows user and their groups.</summary>
    public ICallerIdentityProvider? Caller { get; set; }

    /// <summary>The folder of table configs. Defaults to <see cref="MasquePaths.DefaultConfigs"/>.</summary>
    public string? ConfigFolder { get; set; }

    /// <summary>
    /// The token vault to use as given. When null, the encrypted vault at <see cref="VaultPath"/> is opened, with an
    /// in-memory cache in front of it, and closed when the gate is disposed.
    /// </summary>
    public ITokenVault? Vault { get; set; }

    /// <summary>The encrypted vault file opened when <see cref="Vault"/> is null. Defaults to <see cref="MasquePaths.DefaultVault"/>.</summary>
    public string? VaultPath { get; set; }

    /// <summary>Protects the vault's key when <see cref="Vault"/> is null. Defaults to the current platform's protector.</summary>
    public IKeyProtector? KeyProtector { get; set; }

    /// <summary>How many token pairs the vault cache keeps in memory.</summary>
    public int VaultCacheSize { get; set; } = 100_000;

    /// <summary>The databases the gate can handle, such as <c>new SqliteDialect()</c> or <c>new SqlServerDialect()</c>.</summary>
    public IList<IMasqueDialect> Dialects { get; } = [];

    /// <summary>Classifies new tables and spots sensitive-looking values. Defaults to the standard detectors.</summary>
    public ColumnClassifier? Classifier { get; set; }

    /// <summary>Randomness for new tokens. Defaults to a cryptographic source.</summary>
    public IRandomSource? Random { get; set; }

    /// <summary>Picks the policy by precedence: machine file, code, user file, locked.</summary>
    /// <returns>The effective policy.</returns>
    /// <exception cref="PolicyException">A policy file exists but can't be read.</exception>
    internal MaskingPolicy ResolvePolicy() =>
        PolicyStore.Load(MachinePolicyPath ?? MasquePaths.MachinePolicy)
        ?? Policy
        ?? PolicyStore.Load(PolicyPath ?? MasquePaths.DefaultPolicy)
        ?? MaskingPolicy.Locked;

    /// <summary>Returns the vault to use, and the object to dispose with the gate when the gate opened it.</summary>
    internal (ITokenVault Vault, IDisposable? Owned) ResolveVault()
    {
        if (Vault is not null)
        {
            return (Vault, null);
        }

        var path = VaultPath ?? MasquePaths.DefaultVault;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var vault = SqliteTokenVault.Open(path, KeyProtector ?? KeyProtectors.ForCurrentPlatform());
        return (new CachingTokenVault(vault, VaultCacheSize), vault);
    }
}
