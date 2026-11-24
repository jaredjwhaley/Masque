using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace Masque.EntityFrameworkCore;

/// <summary>
/// The real stored values of masked keys during a save. A loaded entity's masked key holds a token, and EF Core finds
/// the row by it (<c>WHERE Ssn = @p</c>); the key can't be changed in the entity (EF Core forbids changing a tracked
/// key), so the token is swapped for the row's real value in the command's parameters instead, just before EF Core
/// runs each SaveChanges command.
/// </summary>
/// <remarks>Keyed weakly by context, so a save that never reports its end can't keep values alive.</remarks>
internal sealed class MaskedKeyValues
{
    private readonly ConditionalWeakTable<DbContext, IReadOnlyDictionary<string, string>> _saves = [];

    /// <summary>Records the real value behind each masked key token for the context's save that is starting.</summary>
    /// <param name="context">The context being saved.</param>
    /// <param name="realByToken">Each key token and the value its row stores.</param>
    public void Begin(DbContext context, IReadOnlyDictionary<string, string> realByToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(realByToken);

        if (realByToken.Count == 0)
        {
            _saves.Remove(context);
            return;
        }

        _saves.AddOrUpdate(context, realByToken);
    }

    /// <summary>Forgets the context's values once its save ends.</summary>
    /// <param name="context">The context whose save ended.</param>
    public void End(DbContext context) => _saves.Remove(context);

    /// <summary>
    /// Swaps each parameter holding a recorded key token for the row's real value. By the time EF Core builds the
    /// save's commands, tokens in written values are already real values and tokens anywhere else were refused, so a
    /// token still in a parameter is a key EF Core is matching rows on.
    /// </summary>
    /// <param name="context">The context running the command, if EF Core reported one.</param>
    /// <param name="command">A command SaveChanges is about to run.</param>
    public void Apply(DbContext? context, DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (context is null || !_saves.TryGetValue(context, out var realByToken))
        {
            return;
        }

        foreach (DbParameter parameter in command.Parameters)
        {
            if (parameter.Value is string token && realByToken.TryGetValue(token, out var real))
            {
                parameter.Value = real;
            }
        }
    }
}
