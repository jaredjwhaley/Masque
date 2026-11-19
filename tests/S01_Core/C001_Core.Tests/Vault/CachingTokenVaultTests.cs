using System.Diagnostics.CodeAnalysis;
using Masque.Core.Vault;

namespace Masque.Core.Tests.Vault;

/// <summary>Runs the shared vault contract against the caching decorator.</summary>
public class CachingTokenVaultContractTests : TokenVaultContractTests
{
    protected override ITokenVault Vault { get; } = new CachingTokenVault(new InMemoryTokenVault(), capacity: 100);
}

/// <summary>
/// Pins down the cache in front of the vault: repeat lookups are answered from memory, new pairs are written through,
/// and the least recently used entries are dropped once the cache is full, so memory stays bounded.
/// </summary>
public class CachingTokenVaultTests
{
    private readonly CountingVault _inner = new();

    /// <summary>A value looked up twice reaches the real vault once.</summary>
    [Fact]
    public void TryGetToken_Repeated_HitsTheInnerVaultOnce()
    {
        _inner.Add(SensitiveDataType.Ssn, "123456789", "900-11-2222");
        var cache = new CachingTokenVault(_inner, capacity: 10);

        cache.TryGetToken(SensitiveDataType.Ssn, "123456789", out _).ShouldBeTrue();
        cache.TryGetToken(SensitiveDataType.Ssn, "123456789", out var token).ShouldBeTrue();

        token.ShouldBe("900-11-2222");
        _inner.Lookups.ShouldBe(1);
    }

    /// <summary>A reverse lookup (token to value) is cached too.</summary>
    [Fact]
    public void TryGetOriginal_Repeated_HitsTheInnerVaultOnce()
    {
        _inner.Add(SensitiveDataType.Ssn, "123456789", "900-11-2222");
        var cache = new CachingTokenVault(_inner, capacity: 10);

        cache.TryGetOriginal(SensitiveDataType.Ssn, "900-11-2222", out _);
        cache.TryGetOriginal(SensitiveDataType.Ssn, "900-11-2222", out var value).ShouldBeTrue();

        value.ShouldBe("123456789");
        _inner.Lookups.ShouldBe(1);
    }

    /// <summary>New pairs reach the real vault immediately, so nothing is lost if the process stops.</summary>
    [Fact]
    public void Add_WritesThrough()
    {
        new CachingTokenVault(_inner, capacity: 10).Add(SensitiveDataType.Name, "PAT LEE", "Avery Brooks");

        _inner.TryGetToken(SensitiveDataType.Name, "PAT LEE", out var token).ShouldBeTrue();
        token.ShouldBe("Avery Brooks");
    }

    /// <summary>When full, the least recently used entry is dropped and is looked up again next time.</summary>
    [Fact]
    public void Capacity_EvictsLeastRecentlyUsed()
    {
        _inner.Add(SensitiveDataType.Ssn, "1", "900-00-0001");
        _inner.Add(SensitiveDataType.Ssn, "2", "900-00-0002");
        _inner.Add(SensitiveDataType.Ssn, "3", "900-00-0003");
        var cache = new CachingTokenVault(_inner, capacity: 2);

        cache.TryGetToken(SensitiveDataType.Ssn, "1", out _);
        cache.TryGetToken(SensitiveDataType.Ssn, "2", out _);
        cache.TryGetToken(SensitiveDataType.Ssn, "1", out _);
        cache.TryGetToken(SensitiveDataType.Ssn, "3", out _);
        _inner.Lookups = 0;

        cache.TryGetToken(SensitiveDataType.Ssn, "1", out _);
        _inner.Lookups.ShouldBe(0);
        cache.TryGetToken(SensitiveDataType.Ssn, "2", out _);
        _inner.Lookups.ShouldBe(1);
    }

    /// <summary>
    /// The same spelling recorded again (every row that repeats a value) reaches the real vault once, so masking a
    /// large table doesn't write to the vault for every cell.
    /// </summary>
    [Fact]
    public void AddSpelling_Repeated_WritesThroughOnce()
    {
        _inner.Add(SensitiveDataType.Ssn, "123456789", "900-11-2222");
        var cache = new CachingTokenVault(_inner, capacity: 10);

        cache.AddSpelling(SensitiveDataType.Ssn, "900-11-2222", "123-45-6789");
        cache.AddSpelling(SensitiveDataType.Ssn, "900-11-2222", "123-45-6789");

        _inner.SpellingWrites.ShouldBe(1);
        cache.GetSpellings(SensitiveDataType.Ssn, "900-11-2222").ShouldBe(["123-45-6789"]);
    }

    /// <summary>Spellings the real vault already holds (from an earlier run) are read through the cache.</summary>
    [Fact]
    public void GetSpellings_ReadsWhatTheInnerVaultHolds()
    {
        _inner.Add(SensitiveDataType.Ssn, "123456789", "900-11-2222");
        _inner.AddSpelling(SensitiveDataType.Ssn, "900-11-2222", "123-45-6789");
        var cache = new CachingTokenVault(_inner, capacity: 10);

        cache.AddSpelling(SensitiveDataType.Ssn, "900-11-2222", "123 45 6789");

        cache.GetSpellings(SensitiveDataType.Ssn, "900-11-2222").ShouldBe(["123-45-6789", "123 45 6789"], ignoreOrder: true);
    }

    /// <summary>The capacity must be positive.</summary>
    [Fact]
    public void Constructor_ZeroCapacity_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new CachingTokenVault(_inner, capacity: 0));
    }

    /// <summary>An in-memory vault that counts lookups reaching it.</summary>
    private sealed class CountingVault : ITokenVault
    {
        private readonly InMemoryTokenVault _vault = new();

        public int Lookups { get; set; }

        public bool TryGetToken(SensitiveDataType category, string normalizedValue, [NotNullWhen(true)] out string? token)
        {
            Lookups++;
            return _vault.TryGetToken(category, normalizedValue, out token);
        }

        public bool TryGetOriginal(SensitiveDataType category, string token, [NotNullWhen(true)] out string? normalizedValue)
        {
            Lookups++;
            return _vault.TryGetOriginal(category, token, out normalizedValue);
        }

        public bool ContainsToken(SensitiveDataType category, string token) => _vault.ContainsToken(category, token);

        public int SpellingWrites { get; private set; }

        public void Add(SensitiveDataType category, string normalizedValue, string token) => _vault.Add(category, normalizedValue, token);

        public void AddSpelling(SensitiveDataType category, string token, string spelling)
        {
            SpellingWrites++;
            _vault.AddSpelling(category, token, spelling);
        }

        public IReadOnlyCollection<string> GetSpellings(SensitiveDataType category, string token) => _vault.GetSpellings(category, token);
    }
}
