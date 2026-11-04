using System.Security.Cryptography;
using System.Text;

namespace Masque.Vault.Tests.TestSupport;

/// <summary>
/// Stands in for an OS secret store in tests. Not secure: it only tags the key with an owner name, so a
/// protector with a different owner fails to unprotect it, the way another user account would.
/// </summary>
internal sealed class FakeKeyProtector(string owner) : IKeyProtector
{
    private readonly byte[] _tag = Encoding.UTF8.GetBytes(owner + ":");

    public byte[] Protect(byte[] secret) => [.. _tag, .. secret];

    public byte[] Unprotect(byte[] protectedSecret)
    {
        if (!protectedSecret.AsSpan().StartsWith(_tag))
        {
            throw new CryptographicException("Key belongs to another owner.");
        }

        return protectedSecret[_tag.Length..];
    }
}
