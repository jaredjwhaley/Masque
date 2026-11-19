using System.Runtime.CompilerServices;
using Masque.Core;
using Masque.Core.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Masque.EntityFrameworkCore;

/// <summary>
/// Checks SaveChanges before EF Core writes anything: the caller's policy must allow writes, and no modified property
/// may map to a column masked for them. Loaded entities hold tokens in masked properties, so saving one would write a
/// token over the real value. Inserts and deletes are allowed. While the save runs, the connection's write window is
/// open so the database's own guard lets EF Core's statements through.
/// </summary>
/// <param name="sessions">The sessions shared by this context's Masque interceptors.</param>
internal sealed class MasqueSaveChangesInterceptor(MasqueEfSessions sessions) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, IDisposable> _writeWindows = [];

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context && Check(context) is { } changes)
        {
            context.Database.OpenConnection();
            try
            {
                Refuse(context, changes);
            }
            finally
            {
                context.Database.CloseConnection();
            }

            OpenWriteWindow(context);
        }

        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && Check(context) is { } changes)
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Refuse(context, changes);
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }

            OpenWriteWindow(context);
        }

        return result;
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        CloseWriteWindow(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        CloseWriteWindow(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => CloseWriteWindow(eventData.Context);

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        CloseWriteWindow(eventData.Context);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void SaveChangesCanceled(DbContextEventData eventData) => CloseWriteWindow(eventData.Context);

    /// <inheritdoc />
    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        CloseWriteWindow(eventData.Context);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The entries SaveChanges will write, or null when there are none. Refuses the save outright when the caller's
    /// policy doesn't allow writes.
    /// </summary>
    private List<EntityEntry>? Check(DbContext context)
    {
        // Entries() runs change detection when the context has it on, the same detection SaveChanges itself runs.
        var changes = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        if (changes.Count == 0)
        {
            return null;
        }

        if (!sessions.Grant(null).CanWrite)
        {
            throw new QueryRejectedException("This caller may not write: the Masque policy doesn't allow saving changes for them.");
        }

        return changes;
    }

    /// <summary>Refuses the save when a modified property maps to a column masked for the caller.</summary>
    /// <remarks>The context's connection must be open: the masked columns come from the tables' reviewed configs.</remarks>
    private void Refuse(DbContext context, List<EntityEntry> changes)
    {
        var session = sessions.For(context.Database.GetDbConnection());
        session.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        var grant = sessions.Grant(null);
        var protectedByTable = new Dictionary<string, IReadOnlyDictionary<string, SensitiveDataType>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in changes.Where(e => e.State == EntityState.Modified))
        {
            foreach (var property in entry.Properties.Where(p => p.IsModified))
            {
                foreach (var mapping in property.Metadata.GetTableColumnMappings())
                {
                    var table = new TableReference(mapping.TableMapping.Table.Schema, mapping.TableMapping.Table.Name);
                    if (!protectedByTable.TryGetValue(table.Qualified, out var masked))
                    {
                        masked = sessions.Gate.ProtectedColumns(session, table, grant);
                        protectedByTable[table.Qualified] = masked;
                    }

                    if (masked.TryGetValue(mapping.Column.Name, out var type))
                    {
                        throw new QueryRejectedException(
                            $"Can't save {entry.Metadata.DisplayName()}.{property.Metadata.Name}: column {mapping.Column.Name} of " +
                            $"{table.Qualified} is masked for this caller ({type}), so the entity holds a token, not the real value. " +
                            "Change only unmasked properties, or have a caller who sees this column raw make the change.");
                    }
                }
            }
        }
    }

    /// <summary>Lets the context's statements through the database's guard until the save ends.</summary>
    private void OpenWriteWindow(DbContext context)
    {
        CloseWriteWindow(context);
        var session = sessions.For(context.Database.GetDbConnection());
        _writeWindows.Add(context, session.AllowWrites());
    }

    /// <summary>Ends the context's write window, if one is open.</summary>
    private void CloseWriteWindow(DbContext? context)
    {
        if (context is not null && _writeWindows.TryGetValue(context, out var window))
        {
            _writeWindows.Remove(context);
            window.Dispose();
        }
    }
}
