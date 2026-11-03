using System.Security.Cryptography;

namespace Masque.Core;

/// <summary>Source of random numbers for token generation, swappable so tests can be repeatable.</summary>
public interface IRandomSource
{
    /// <summary>Returns a random integer from 0 up to, but not including, <paramref name="maxExclusive"/>.</summary>
    /// <param name="maxExclusive">Upper bound, exclusive. Must be greater than zero.</param>
    /// <returns>A value in the range [0, <paramref name="maxExclusive"/>).</returns>
    int Next(int maxExclusive);
}

/// <summary>
/// Cryptographically strong random source. Use this outside tests so tokens can't be predicted from each other.
/// </summary>
public sealed class CryptoRandomSource : IRandomSource
{
    /// <summary>Shared instance; the class holds no state.</summary>
    public static CryptoRandomSource Shared { get; } = new();

    /// <inheritdoc />
    public int Next(int maxExclusive) => RandomNumberGenerator.GetInt32(maxExclusive);
}
