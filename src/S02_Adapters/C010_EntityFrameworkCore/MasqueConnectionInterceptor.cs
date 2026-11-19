using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Masque.EntityFrameworkCore;

/// <summary>
/// Adds the database's own Masque guard (such as SQLite's authorizer) each time EF Core opens a connection, and
/// forgets the connection's table list when EF Core closes it, since it may reopen on another database.
/// </summary>
/// <param name="sessions">The sessions shared by this context's Masque interceptors.</param>
internal sealed class MasqueConnectionInterceptor(MasqueEfSessions sessions) : DbConnectionInterceptor
{
    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Secure(connection);

    /// <inheritdoc />
    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Secure(connection);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) => Reset(connection);

    /// <inheritdoc />
    public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {
        Reset(connection);
        return Task.CompletedTask;
    }

    /// <summary>Guards a newly opened connection; the guard reads the session's write window on every write.</summary>
    private void Secure(DbConnection connection)
    {
        var session = sessions.For(connection);
        session.Dialect.Secure(connection, () => session.WritesAllowed);
    }

    /// <summary>Forgets what the session learned about the database while the connection was open.</summary>
    private void Reset(DbConnection connection)
    {
        if (sessions.TryGet(connection, out var session))
        {
            session.Reset();
        }
    }
}
