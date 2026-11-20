using System.Data.Common;
using Masque.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Masque.EntityFrameworkCore;

/// <summary>
/// Runs every command EF Core sends (LINQ queries, raw SQL, <c>ExecuteUpdate</c> and <c>ExecuteDelete</c>) through the
/// Masque gate: the statement is checked first, tokens the caller filters on are swapped for real values, and readers
/// and scalars come back masked. The interceptor runs the command itself and hands EF Core the result.
/// </summary>
/// <remarks>
/// Commands SaveChanges sends are left to <see cref="MasqueSaveChangesInterceptor"/>, which applies the write rule to the tracked changes
/// instead: EF Core's batched INSERT ... RETURNING statements are shapes the SQL check refuses on purpose.
/// </remarks>
/// <param name="sessions">The sessions shared by this context's Masque interceptors.</param>
internal sealed class MasqueCommandInterceptor(MasqueEfSessions sessions) : DbCommandInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
        Skip(eventData, result.HasResult) ? result : InterceptionResult<DbDataReader>.SuppressWithResult(ExecuteReader(command));

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
        Skip(eventData, result.HasResult)
            ? result
            : InterceptionResult<DbDataReader>.SuppressWithResult(await ExecuteReaderAsync(command, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        if (Skip(eventData, result.HasResult))
        {
            return result;
        }

        using var reader = ExecuteReader(command);
        return InterceptionResult<object>.SuppressWithResult(reader.Read() && reader.FieldCount > 0 ? reader.GetValue(0) : null!);
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        if (Skip(eventData, result.HasResult))
        {
            return result;
        }

        await using var reader = await ExecuteReaderAsync(command, cancellationToken).ConfigureAwait(false);
        var hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return InterceptionResult<object>.SuppressWithResult(hasRow && reader.FieldCount > 0 ? reader.GetValue(0) : null!);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        if (Skip(eventData, result.HasResult))
        {
            return result;
        }

        using var prepared = Prepare(command);
        try
        {
            return InterceptionResult<int>.SuppressWithResult(command.ExecuteNonQuery());
        }
        catch (DbException ex)
        {
            throw prepared.Session.Dialect.Describe(ex);
        }
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Skip(eventData, result.HasResult))
        {
            return result;
        }

        using var prepared = Prepare(command);
        try
        {
            return InterceptionResult<int>.SuppressWithResult(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (DbException ex)
        {
            throw prepared.Session.Dialect.Describe(ex);
        }
    }

    /// <summary>
    /// Whether to let EF Core run the command untouched: SaveChanges commands (checked by the SaveChanges
    /// interceptor) and commands another interceptor already answered.
    /// </summary>
    private static bool Skip(CommandEventData eventData, bool alreadyAnswered) =>
        alreadyAnswered || eventData.CommandSource == CommandSource.SaveChanges;

    /// <summary>Checks the command and runs it, returning a masking reader that owns the prepared command.</summary>
    private DbDataReader ExecuteReader(DbCommand command)
    {
        var prepared = Prepare(command);
        DbDataReader reader;
        try
        {
            reader = command.ExecuteReader(prepared.Session.Dialect.LineageBehavior);
        }
        catch (DbException ex)
        {
            prepared.Dispose();
            throw prepared.Session.Dialect.Describe(ex);
        }
        catch
        {
            prepared.Dispose();
            throw;
        }

        prepared.EndWrites();
        return sessions.Gate.WrapReader(reader, prepared);
    }

    /// <summary>Checks the command and runs it asynchronously, returning a masking reader that owns the prepared command.</summary>
    private async Task<DbDataReader> ExecuteReaderAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var prepared = Prepare(command);
        DbDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(prepared.Session.Dialect.LineageBehavior, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            prepared.Dispose();
            throw prepared.Session.Dialect.Describe(ex);
        }
        catch
        {
            prepared.Dispose();
            throw;
        }

        prepared.EndWrites();
        return sessions.Gate.WrapReader(reader, prepared);
    }

    /// <summary>
    /// Checks the statement for the current caller and readies the raw command, joining the transaction EF Core gave
    /// it. The command's text and parameters are restored when the result is disposed.
    /// </summary>
    private PreparedCommand Prepare(DbCommand command)
    {
        var connection = command.Connection
            ?? throw new InvalidOperationException("EF Core sent a command with no connection.");
        var session = sessions.For(connection);

        // EF Core sets the command's transaction whenever one is active, so it is the connection's current one.
        session.Transaction = command.Transaction;

        var text = command.CommandText;
        return sessions.Gate.Prepare(command, text, command.CommandType, session, sessions.Grant(text));
    }
}
