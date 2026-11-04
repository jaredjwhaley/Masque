using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Masque.Vault;

/// <summary>
/// Wraps the vault's secret key so only the current user account can unwrap it. Backed by the operating
/// system's secret store, so the key never sits on disk in the clear.
/// </summary>
public interface IKeyProtector
{
    /// <summary>Wraps a secret so it can be stored on disk.</summary>
    /// <param name="secret">The raw key bytes.</param>
    /// <returns>The wrapped bytes, safe to store.</returns>
    byte[] Protect(byte[] secret);

    /// <summary>Unwraps a secret produced by <see cref="Protect"/>.</summary>
    /// <param name="protectedSecret">The wrapped bytes.</param>
    /// <returns>The raw key bytes.</returns>
    /// <exception cref="CryptographicException">The bytes were wrapped by another user or machine, or are damaged.</exception>
    byte[] Unprotect(byte[] protectedSecret);
}

/// <summary>
/// Windows key protection through DPAPI (Data Protection API): the key is encrypted with secrets tied to the
/// signed-in Windows user, so another account, or a copy of the file on another PC, can't unwrap it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    // Extra input DPAPI mixes in, so other apps using DPAPI for the same user can't unwrap Masque's key by accident.
    private static readonly byte[] Entropy = "Masque vault key v1"u8.ToArray();

    /// <inheritdoc />
    public byte[] Protect(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return ProtectedData.Protect(secret, Entropy, DataProtectionScope.CurrentUser);
    }

    /// <inheritdoc />
    public byte[] Unprotect(byte[] protectedSecret)
    {
        ArgumentNullException.ThrowIfNull(protectedSecret);
        return ProtectedData.Unprotect(protectedSecret, Entropy, DataProtectionScope.CurrentUser);
    }
}

/// <summary>Picks the key protector for the operating system Masque is running on.</summary>
public static class KeyProtectors
{
    /// <summary>Returns the OS secret-store protector for this platform.</summary>
    /// <returns>A protector backed by the OS secret store.</returns>
    /// <exception cref="PlatformNotSupportedException">No protector exists yet for this OS (macOS and Linux are planned).</exception>
    public static IKeyProtector ForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new DpapiKeyProtector();
        }

        throw new PlatformNotSupportedException(
            "Masque can only protect its vault key on Windows so far. macOS Keychain and Linux libsecret support are planned.");
    }
}
