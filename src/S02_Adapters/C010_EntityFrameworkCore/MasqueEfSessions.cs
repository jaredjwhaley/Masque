using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Masque.Core;
using Masque.Core.Policy;
using Masque.Data;

namespace Masque.EntityFrameworkCore;

/// <summary>
/// What the Masque interceptors registered on one set of context options share: the gate, the options' raw-type allow
/// list, and one <see cref="MasqueSession"/> per raw connection EF Core uses.
/// </summary>
/// <remarks>
/// Sessions are keyed weakly by connection, so they live exactly as long as the connection does. Grants are worked
/// out per command rather than per connection, because pooled contexts and a changing caller can share a connection.
/// </remarks>
internal sealed class MasqueEfSessions(MasqueGate gate, IReadOnlyCollection<SensitiveDataType>? allowRaw)
{
    private readonly ConditionalWeakTable<DbConnection, MasqueSession> _sessions = [];

    /// <summary>The gate that checks statements and masks results.</summary>
    public MasqueGate Gate { get; } = gate;

    /// <summary>The real values of masked keys for saves in progress, shared by the SaveChanges and command interceptors.</summary>
    public MaskedKeyValues Keys { get; } = new();

    /// <summary>
    /// The session for a raw connection, started on first use. A connection that is already open (one handed to
    /// EF Core open) gets the dialect's guard now, since EF Core won't report opening it.
    /// </summary>
    /// <param name="connection">The raw connection.</param>
    /// <returns>Its session.</returns>
    public MasqueSession For(DbConnection connection) =>
        _sessions.GetValue(connection, raw =>
        {
            var session = Gate.Attach(raw);
            if (raw.State == ConnectionState.Open)
            {
                session.Dialect.Secure(raw, () => session.WritesAllowed);
            }

            return session;
        });

    /// <summary>The session for a raw connection, if one was started.</summary>
    /// <param name="connection">The raw connection.</param>
    /// <param name="session">Its session.</param>
    /// <returns><see langword="true"/> when the connection has a session.</returns>
    public bool TryGet(DbConnection connection, out MasqueSession session) => _sessions.TryGetValue(connection, out session!);

    /// <summary>
    /// What the current caller may do for one command: their policy, narrowed by the options' allow list and by any
    /// <c>-- masque: allow</c> tag (EF Core's <c>TagWith</c>) in the SQL.
    /// </summary>
    /// <param name="commandText">The SQL, or null when no command is involved (SaveChanges checks).</param>
    /// <returns>The grant.</returns>
    public AccessGrant Grant(string? commandText) => MasqueGate.GrantFor(Gate.CurrentGrant(allowRaw), null, commandText);
}
