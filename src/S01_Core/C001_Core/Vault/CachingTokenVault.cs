using System.Diagnostics.CodeAnalysis;

namespace Masque.Core.Vault;

/// <summary>
/// Keeps recently used value/token pairs and spellings in memory in front of another vault, so repeated values skip
/// the encrypted store (the Decorator pattern: same interface, added behaviour). New pairs and spellings are written
/// through at once. Memory is bounded: once <c>capacity</c> entries are cached in a map, the least recently used is
/// dropped. Thread-safe.
/// </summary>
/// <remarks>Real values live in this process's memory while cached, as they already do while rows are read.</remarks>
public sealed class CachingTokenVault : ITokenVault
{
    private readonly ITokenVault _inner;
    private readonly LruCache<(SensitiveDataType, string), string> _tokensByValue;
    private readonly LruCache<(SensitiveDataType, string), string> _valuesByToken;
    private readonly LruCache<(SensitiveDataType, string), HashSet<string>> _spellingsByToken;
    private readonly Lock _lock = new();

    /// <summary>Wraps a vault with a cache.</summary>
    /// <param name="inner">The vault that stores pairs durably.</param>
    /// <param name="capacity">Most pairs to cache in each direction; at least 1.</param>
    public CachingTokenVault(ITokenVault inner, int capacity = 100_000)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _inner = inner;
        _tokensByValue = new(capacity);
        _valuesByToken = new(capacity);
        _spellingsByToken = new(capacity);
    }

    /// <inheritdoc />
    public bool TryGetToken(SensitiveDataType category, string normalizedValue, [NotNullWhen(true)] out string? token)
    {
        lock (_lock)
        {
            if (_tokensByValue.TryGet((category, normalizedValue), out token))
            {
                return true;
            }

            if (!_inner.TryGetToken(category, normalizedValue, out token))
            {
                return false;
            }

            Remember(category, normalizedValue, token);
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryGetOriginal(SensitiveDataType category, string token, [NotNullWhen(true)] out string? normalizedValue)
    {
        lock (_lock)
        {
            if (_valuesByToken.TryGet((category, token), out normalizedValue))
            {
                return true;
            }

            if (!_inner.TryGetOriginal(category, token, out normalizedValue))
            {
                return false;
            }

            Remember(category, normalizedValue, token);
            return true;
        }
    }

    /// <inheritdoc />
    public bool ContainsToken(SensitiveDataType category, string token)
    {
        lock (_lock)
        {
            return _valuesByToken.TryGet((category, token), out _) || _inner.ContainsToken(category, token);
        }
    }

    /// <inheritdoc />
    public void Add(SensitiveDataType category, string normalizedValue, string token)
    {
        lock (_lock)
        {
            _inner.Add(category, normalizedValue, token);
            Remember(category, normalizedValue, token);
        }
    }

    /// <inheritdoc />
    /// <remarks>A spelling already cached for the token is not written again, so repeated values cost no vault write.</remarks>
    public void AddSpelling(SensitiveDataType category, string token, string spelling)
    {
        ArgumentNullException.ThrowIfNull(spelling);

        lock (_lock)
        {
            var spellings = CachedSpellings(category, token);
            if (spellings.Contains(spelling))
            {
                return;
            }

            // Write through first: if the inner vault refuses (unknown token), the cache must not claim the spelling.
            _inner.AddSpelling(category, token, spelling);
            spellings.Add(spelling);
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> GetSpellings(SensitiveDataType category, string token)
    {
        lock (_lock)
        {
            return [.. CachedSpellings(category, token)];
        }
    }

    /// <summary>The token's spellings, loaded from the inner vault on first use. Call inside the lock.</summary>
    private HashSet<string> CachedSpellings(SensitiveDataType category, string token)
    {
        if (!_spellingsByToken.TryGet((category, token), out var spellings))
        {
            spellings = new HashSet<string>(_inner.GetSpellings(category, token), StringComparer.Ordinal);
            _spellingsByToken.Set((category, token), spellings);
        }

        return spellings;
    }

    private void Remember(SensitiveDataType category, string normalizedValue, string token)
    {
        _tokensByValue.Set((category, normalizedValue), token);
        _valuesByToken.Set((category, token), normalizedValue);
    }

    /// <summary>A bounded map that drops its least recently used entry when full. Not thread-safe on its own.</summary>
    private sealed class LruCache<TKey, TValue>(int capacity)
        where TKey : notnull
    {
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _nodes = [];
        private readonly LinkedList<(TKey Key, TValue Value)> _order = new();

        /// <summary>Looks up a key and marks it most recently used.</summary>
        public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            if (!_nodes.TryGetValue(key, out var node))
            {
                value = default;
                return false;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }

        /// <summary>Adds or replaces a key, dropping the least recently used entry when full.</summary>
        public void Set(TKey key, TValue value)
        {
            if (_nodes.Remove(key, out var existing))
            {
                _order.Remove(existing);
            }
            else if (_nodes.Count >= capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _nodes.Remove(oldest.Value.Key);
            }

            _nodes[key] = _order.AddFirst((key, value));
        }
    }
}
