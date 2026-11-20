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
/// Applies Masque's write rule to SaveChanges, the same rule plain SQL writes follow. The caller's policy must allow
/// writes. Masque prevents exposure, not change, so any property may be saved with a value the caller supplies; but a
/// loaded entity holds tokens in masked properties, so each token about to be written into a masked column of its
/// type is swapped for the real value it stands for, and put back once the save ends, so the caller's entities never
/// hold a real value. A token about to be written anywhere else is refused. While the save runs, the connection's
/// write window is open so the database's own guard lets EF Core's statements through.
/// </summary>
/// <param name="sessions">The sessions shared by this context's Masque interceptors.</param>
internal sealed class MasqueSaveChangesInterceptor(MasqueEfSessions sessions) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, SaveInProgress> _saves = [];

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context && Check(context) is { } changes)
        {
            IReadOnlyList<TokenSwap> swaps;
            context.Database.OpenConnection();
            try
            {
                swaps = FindTokenSwaps(context, changes);
            }
            finally
            {
                context.Database.CloseConnection();
            }

            Begin(context, swaps);
        }

        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && Check(context) is { } changes)
        {
            IReadOnlyList<TokenSwap> swaps;
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                swaps = FindTokenSwaps(context, changes);
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }

            Begin(context, swaps);
        }

        return result;
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        End(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        End(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => End(eventData.Context);

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        End(eventData.Context);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void SaveChangesCanceled(DbContextEventData eventData) => End(eventData.Context);

    /// <inheritdoc />
    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        End(eventData.Context);
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

    /// <summary>
    /// Finds every token about to be written and the real value to write instead. Nothing is changed here, so a refusal
    /// leaves the entities exactly as they were.
    /// </summary>
    /// <remarks>The context's connection must be open: the masked columns come from the tables' reviewed configs.</remarks>
    /// <exception cref="QueryRejectedException">A token is about to be written outside a masked column of its own type.</exception>
    private List<TokenSwap> FindTokenSwaps(DbContext context, List<EntityEntry> changes)
    {
        var session = sessions.For(context.Database.GetDbConnection());
        session.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        var grant = sessions.Grant(null);
        var maskedByTable = new Dictionary<string, IReadOnlyDictionary<string, SensitiveDataType>>(StringComparer.OrdinalIgnoreCase);

        // The type a property's column is masked as for this caller, or null when no column it maps to is masked.
        SensitiveDataType? MaskedType(IProperty property)
        {
            foreach (var mapping in property.GetTableColumnMappings())
            {
                var table = new TableReference(mapping.TableMapping.Table.Schema, mapping.TableMapping.Table.Name);
                if (!maskedByTable.TryGetValue(table.Qualified, out var masked))
                {
                    masked = sessions.Gate.ProtectedColumns(session, table, grant);
                    maskedByTable[table.Qualified] = masked;
                }

                if (masked.TryGetValue(mapping.Column.Name, out var type))
                {
                    return type;
                }
            }

            return null;
        }

        var swaps = new List<TokenSwap>();
        foreach (var entry in changes.Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            // An insert writes every property; an update writes only the modified ones.
            var written = entry.Properties.Where(p => entry.State == EntityState.Added || p.IsModified);
            foreach (var property in written)
            {
                if (property.CurrentValue is string text
                    && sessions.Gate.ResolveWrittenValue(MaskedType(property.Metadata), text) is { } real)
                {
                    swaps.Add(new TokenSwap(property, text, real));
                }
            }
        }

        return swaps;
    }

    /// <summary>Puts the real values in place and lets the context's statements through the database's guard until the save ends.</summary>
    private void Begin(DbContext context, IReadOnlyList<TokenSwap> swaps)
    {
        End(context);
        var window = sessions.For(context.Database.GetDbConnection()).AllowWrites();
        _saves.Add(context, new SaveInProgress(window, swaps));

        foreach (var swap in swaps)
        {
            swap.Property.CurrentValue = swap.RealValue;
        }
    }

    /// <summary>
    /// Ends the context's save, if one is in progress: puts each token back in its entity and closes the write window.
    /// After a successful save the property is marked unchanged again; after a failure it stays marked for saving, and
    /// the next save swaps it again.
    /// </summary>
    private void End(DbContext? context)
    {
        if (context is null || !_saves.TryGetValue(context, out var save))
        {
            return;
        }

        _saves.Remove(context);
        try
        {
            foreach (var swap in save.Swaps)
            {
                var accepted = swap.Property.EntityEntry.State == EntityState.Unchanged;
                swap.Property.CurrentValue = swap.Token;
                if (accepted)
                {
                    swap.Property.OriginalValue = swap.Token;
                    swap.Property.IsModified = false;
                }
            }
        }
        finally
        {
            save.WriteWindow.Dispose();
        }
    }

    /// <summary>A token about to be written, and the real value written in its place.</summary>
    /// <param name="Property">The property being written.</param>
    /// <param name="Token">The token the entity held, put back after the save.</param>
    /// <param name="RealValue">The value written instead.</param>
    private sealed record TokenSwap(PropertyEntry Property, string Token, string RealValue);

    /// <summary>What a running save must undo when it ends.</summary>
    /// <param name="WriteWindow">Closes the connection's write window.</param>
    /// <param name="Swaps">The tokens to put back.</param>
    private sealed record SaveInProgress(IDisposable WriteWindow, IReadOnlyList<TokenSwap> Swaps);
}
