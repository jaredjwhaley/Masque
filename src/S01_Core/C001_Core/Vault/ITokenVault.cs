using System.Diagnostics.CodeAnalysis;

namespace Masque.Core.Vault;

/// <summary>
/// Two-way map between real (normalized) values and their tokens, scoped by category.
/// The vault is the only place real values are kept; it must never leave the local machine.
/// </summary>
public interface ITokenVault
{
    /// <summary>Finds the token already assigned to a real value.</summary>
    /// <param name="category">Category of the value.</param>
    /// <param name="normalizedValue">The real value after normalization.</param>
    /// <param name="token">The token, when found.</param>
    /// <returns><see langword="true"/> when the value already has a token.</returns>
    bool TryGetToken(SensitiveDataType category, string normalizedValue, [NotNullWhen(true)] out string? token);

    /// <summary>Finds the real value behind a token, for local reveal only.</summary>
    /// <param name="category">Category of the token.</param>
    /// <param name="token">The token to look up.</param>
    /// <param name="normalizedValue">The real value, when found.</param>
    /// <returns><see langword="true"/> when the token is known.</returns>
    bool TryGetOriginal(SensitiveDataType category, string token, [NotNullWhen(true)] out string? normalizedValue);

    /// <summary>Checks whether a token is already assigned to some value.</summary>
    /// <param name="category">Category of the token.</param>
    /// <param name="token">The candidate token.</param>
    /// <returns><see langword="true"/> when the token is taken.</returns>
    bool ContainsToken(SensitiveDataType category, string token);

    /// <summary>Stores a new value-to-token pair.</summary>
    /// <param name="category">Category of the pair.</param>
    /// <param name="normalizedValue">The real value after normalization.</param>
    /// <param name="token">Its token.</param>
    /// <exception cref="InvalidOperationException">The value or the token is already stored for this category.</exception>
    void Add(SensitiveDataType category, string normalizedValue, string token);

    /// <summary>
    /// Records one way a token's value is written in the data: its spelling before normalization, such as
    /// <c>123-45-6789</c> for the normalized SSN <c>123456789</c>. A filter on the token then matches the value however
    /// a source stores it. Recording a spelling that is already held does nothing.
    /// </summary>
    /// <param name="category">Category of the token.</param>
    /// <param name="token">A token the vault already holds.</param>
    /// <param name="spelling">The value exactly as a data source stores it.</param>
    /// <exception cref="InvalidOperationException">The vault holds no such token for this category.</exception>
    void AddSpelling(SensitiveDataType category, string token, string spelling);

    /// <summary>Lists the spellings recorded for a token, for local filter rewriting only.</summary>
    /// <param name="category">Category of the token.</param>
    /// <param name="token">The token.</param>
    /// <returns>Each recorded spelling once, in no set order; empty for an unknown token.</returns>
    IReadOnlyCollection<string> GetSpellings(SensitiveDataType category, string token);
}
