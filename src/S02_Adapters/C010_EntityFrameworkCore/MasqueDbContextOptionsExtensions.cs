using Masque.Core;
using Masque.Data;
using Masque.EntityFrameworkCore;

// Same namespace as UseSqlite and UseSqlServer, so UseMasque shows up wherever a context is configured.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Registers Masque on an EF Core context.</summary>
public static class MasqueDbContextOptionsExtensions
{
    /// <summary>
    /// Runs the context's queries and saves through Masque: LINQ and raw SQL results come back with sensitive columns
    /// tokenized for the current caller, statements that would leak masked values are refused, and SaveChanges
    /// refuses to change a property the caller only sees as a token. Which columns are sensitive comes from the
    /// person-reviewed table configs the gate reads, never from the model.
    /// </summary>
    /// <param name="optionsBuilder">The context's options.</param>
    /// <param name="gate">The gate holding the policy, configs and vault.</param>
    /// <param name="allowRaw">
    /// Raw types to allow for this context; narrows the caller's policy and never widens it. Null keeps the policy.
    /// A query can narrow further with <c>TagWith("masque: allow Email")</c>.
    /// </param>
    /// <returns>The same options builder, for chaining.</returns>
    public static DbContextOptionsBuilder UseMasque(
        this DbContextOptionsBuilder optionsBuilder, MasqueGate gate, IEnumerable<SensitiveDataType>? allowRaw = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(gate);

        var sessions = new MasqueEfSessions(gate, allowRaw?.ToArray());
        return optionsBuilder.AddInterceptors(
            new MasqueConnectionInterceptor(sessions),
            new MasqueCommandInterceptor(sessions),
            new MasqueSaveChangesInterceptor(sessions));
    }

    /// <summary>
    /// Runs the context's queries and saves through Masque; see
    /// <see cref="UseMasque(DbContextOptionsBuilder, MasqueGate, IEnumerable{SensitiveDataType}?)"/>.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The context's options.</param>
    /// <param name="gate">The gate holding the policy, configs and vault.</param>
    /// <param name="allowRaw">Raw types to allow for this context; narrows the caller's policy and never widens it.</param>
    /// <returns>The same options builder, for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseMasque<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder, MasqueGate gate, IEnumerable<SensitiveDataType>? allowRaw = null)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseMasque((DbContextOptionsBuilder)optionsBuilder, gate, allowRaw);
}
