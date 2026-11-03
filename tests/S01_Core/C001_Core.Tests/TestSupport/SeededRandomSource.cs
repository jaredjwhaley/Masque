namespace Masque.Core.Tests.TestSupport;

/// <summary>Repeatable random source so test runs produce the same tokens every time.</summary>
internal sealed class SeededRandomSource(int seed = 42) : IRandomSource
{
    private readonly Random _random = new(seed);

    public int Next(int maxExclusive) => _random.Next(maxExclusive);
}
