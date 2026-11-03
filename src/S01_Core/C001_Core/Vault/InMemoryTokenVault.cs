using System.Diagnostics.CodeAnalysis;

namespace Masque.Core.Vault;

/// <summary>
/// Vault held in memory for one process. Use it for tests and one-off runs; tokens are lost when the process ends.
/// Not thread-safe.
/// </summary>
public sealed class InMemoryTokenVault : ITokenVault
{
    private readonly Dictionary<(PiiCategory, string), string> _tokensByValue = [];
    private readonly Dictionary<(PiiCategory, string), string> _valuesByToken = [];

    /// <inheritdoc />
    public bool TryGetToken(PiiCategory category, string normalizedValue, [NotNullWhen(true)] out string? token) =>
        _tokensByValue.TryGetValue((category, normalizedValue), out token);

    /// <inheritdoc />
    public bool TryGetOriginal(PiiCategory category, string token, [NotNullWhen(true)] out string? normalizedValue) =>
        _valuesByToken.TryGetValue((category, token), out normalizedValue);

    /// <inheritdoc />
    public bool ContainsToken(PiiCategory category, string token) => _valuesByToken.ContainsKey((category, token));

    /// <inheritdoc />
    public void Add(PiiCategory category, string normalizedValue, string token)
    {
        ArgumentNullException.ThrowIfNull(normalizedValue);
        ArgumentNullException.ThrowIfNull(token);

        // Check both directions first so a failed add leaves the vault unchanged.
        if (_tokensByValue.ContainsKey((category, normalizedValue)))
        {
            throw new InvalidOperationException($"This {category} value already has a token.");
        }

        if (_valuesByToken.ContainsKey((category, token)))
        {
            throw new InvalidOperationException($"This {category} token is already assigned.");
        }

        _tokensByValue.Add((category, normalizedValue), token);
        _valuesByToken.Add((category, token), normalizedValue);
    }
}
