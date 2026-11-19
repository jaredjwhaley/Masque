namespace Masque.Core.Policy;

/// <summary>
/// What one caller may do: which sensitive types they see as real values, and whether they may write. Everything
/// sensitive that is not listed comes back as tokens. A grant can only be narrowed, never widened, by a request.
/// </summary>
public sealed class AccessGrant
{
    /// <summary>Creates a grant.</summary>
    /// <param name="rawTypes">Sensitive types the caller sees as real values.</param>
    /// <param name="canWrite">Whether the caller may run INSERT, UPDATE and DELETE.</param>
    public AccessGrant(IEnumerable<SensitiveDataType> rawTypes, bool canWrite)
    {
        ArgumentNullException.ThrowIfNull(rawTypes);

        RawTypes = rawTypes.ToHashSet();
        CanWrite = canWrite;
    }

    /// <summary>Sees nothing sensitive raw and may not write. The fail-closed default.</summary>
    public static AccessGrant None { get; } = new([], canWrite: false);

    /// <summary>Sensitive types the caller sees as real values.</summary>
    public IReadOnlySet<SensitiveDataType> RawTypes { get; }

    /// <summary>Whether the caller may run INSERT, UPDATE and DELETE.</summary>
    public bool CanWrite { get; }

    /// <summary>Whether values of a type reach the caller unmasked: non-sensitive types always do.</summary>
    /// <param name="type">The column's type.</param>
    /// <returns><see langword="true"/> when the type is shown raw.</returns>
    public bool Shows(SensitiveDataType type) => !type.IsSensitive() || RawTypes.Contains(type);

    /// <summary>Limits the grant to the types a request asks for; types not granted stay masked.</summary>
    /// <param name="requested">Types the request wants raw, or <see langword="null"/> for no limit.</param>
    /// <returns>This grant when <paramref name="requested"/> is null; otherwise the intersection.</returns>
    public AccessGrant Narrow(IEnumerable<SensitiveDataType>? requested) =>
        requested is null ? this : new AccessGrant(RawTypes.Intersect(requested), CanWrite);

    /// <summary>Combines grants: every raw type from any of them, and writes if any allows them.</summary>
    /// <param name="grants">The grants to combine; at least one.</param>
    /// <returns>The combined grant.</returns>
    public static AccessGrant Combine(IReadOnlyCollection<AccessGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        return new AccessGrant(grants.SelectMany(g => g.RawTypes), grants.Any(g => g.CanWrite));
    }
}
