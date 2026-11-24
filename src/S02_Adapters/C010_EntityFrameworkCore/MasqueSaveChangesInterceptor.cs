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
/// <remarks>
/// <para>
/// A token stands for one value that the data may store in several spellings (<c>900-11-1111</c>, <c>900111111</c>).
/// So before an update or delete whose entity holds tokens in its key or in written masked properties, the row is read
/// once (never shown to the caller): a masked key token becomes the key the row really stores, and a token written
/// back into a row that already stores a spelling of it keeps that spelling.
/// </para>
/// <para>
/// When a masked key's token matches more than one row (one value stored twice in different spellings), the entity
/// can't say which row it came from, so the save is refused before anything is written.
/// </para>
/// </remarks>
/// <param name="sessions">The sessions shared by this context's Masque interceptors.</param>
internal sealed class MasqueSaveChangesInterceptor(MasqueEfSessions sessions) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, SaveInProgress> _saves = [];

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context && Check(context) is { } changes)
        {
            SavePlan plan;
            context.Database.OpenConnection();
            try
            {
                plan = PlanSave(context, changes);
            }
            finally
            {
                context.Database.CloseConnection();
            }

            Begin(context, plan);
        }

        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && Check(context) is { } changes)
        {
            SavePlan plan;
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                plan = PlanSave(context, changes);
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }

            Begin(context, plan);
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
    /// Works out every token about to be written and the real value to write instead, and the real value behind each
    /// masked key token. Nothing is changed here, so a refusal leaves the entities exactly as they were.
    /// </summary>
    /// <remarks>The context's connection must be open: masked columns come from the tables' reviewed configs, and rows are read.</remarks>
    /// <exception cref="QueryRejectedException">
    /// A token is about to be written outside a masked column of its own type, or a masked key matches several rows.
    /// </exception>
    private SavePlan PlanSave(DbContext context, List<EntityEntry> changes)
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
        var realKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in changes)
        {
            // An insert has no row yet; an update or delete finds its row by key.
            var stored = entry.State == EntityState.Added ? null : ReadStoredRow(context, entry, MaskedType, realKeys);
            if (entry.State == EntityState.Deleted)
            {
                continue;
            }

            // An insert writes every property; an update writes only the modified ones.
            var written = entry.Properties.Where(p => entry.State == EntityState.Added || p.IsModified);
            foreach (var property in written)
            {
                if (property.CurrentValue is string text
                    && RealValueToWrite(MaskedType(property.Metadata), text, stored, property.Metadata) is { } real)
                {
                    swaps.Add(new TokenSwap(property, text, real));
                }
            }
        }

        return new SavePlan(swaps, realKeys);
    }

    /// <summary>
    /// The real value to write for a property's value: the row's own spelling when the row already stores a form of
    /// the token being written; otherwise whatever the gate's write rule decides.
    /// </summary>
    /// <returns>The value to write instead, or null when <paramref name="text"/> isn't a token and is written as given.</returns>
    /// <exception cref="QueryRejectedException">The value is a token, written outside a masked column of its own type.</exception>
    private string? RealValueToWrite(
        SensitiveDataType? maskedType, string text, IReadOnlyDictionary<IProperty, object?>? stored, IProperty property)
    {
        if (maskedType is { } type
            && stored is not null
            && stored.TryGetValue(property, out var current)
            && current is string spelling
            && sessions.Gate.StoredForms(type, text) is { } forms
            && forms.Contains(spelling, StringComparer.Ordinal))
        {
            return spelling;
        }

        return sessions.Gate.ResolveWrittenValue(maskedType, text);
    }

    /// <summary>
    /// Reads, from an updated or deleted entity's row, the masked columns that hold tokens in its key or in its written
    /// properties, and records the real value behind each masked key token in <paramref name="realKeys"/>.
    /// </summary>
    /// <returns>
    /// The stored value of each property read, or null when nothing needs reading or no row matches (a row changed or
    /// deleted since it was loaded: EF Core then reports the save as a concurrency failure, which is what happened).
    /// </returns>
    /// <exception cref="QueryRejectedException">The entity's masked key matches more than one row.</exception>
    private IReadOnlyDictionary<IProperty, object?>? ReadStoredRow(
        DbContext context, EntityEntry entry, Func<IProperty, SensitiveDataType?> maskedType, Dictionary<string, string> realKeys)
    {
        if (StoreObjectIdentifier.Create(entry.Metadata, StoreObjectType.Table) is not { } table
            || entry.Metadata.FindPrimaryKey() is not { } primaryKey)
        {
            return null;
        }

        // A masked key holding a token may match any form its real value is stored in; any other key matches itself.
        var conditions = new List<KeyCondition>(primaryKey.Properties.Count);
        var tokenKeys = new List<(IProperty Property, string Token)>();
        foreach (var key in primaryKey.Properties)
        {
            var original = entry.Property(key).OriginalValue;
            if (key.GetColumnName(table) is not { } column || original is null)
            {
                return null;
            }

            if (original is string text && maskedType(key) is { } type && sessions.Gate.StoredForms(type, text) is { } forms)
            {
                conditions.Add(new KeyCondition(column, [.. forms]));
                tokenKeys.Add((key, text));
            }
            else
            {
                conditions.Add(new KeyCondition(column, [original]));
            }
        }

        // Written masked properties holding a token of their type, whose row spelling should be kept.
        var tokenWrites = entry.State != EntityState.Modified
            ? []
            : entry.Properties
                .Where(p => p.IsModified
                    && p.CurrentValue is string text
                    && maskedType(p.Metadata) is { } type
                    && sessions.Gate.StoredForms(type, text) is not null)
                .Select(p => p.Metadata)
                .ToList();

        var toRead = tokenKeys.Select(k => k.Property)
            .Concat(tokenWrites)
            .Distinct()
            .Select(p => (Property: p, Column: p.GetColumnName(table)))
            .Where(p => p.Column is not null)
            .ToList();
        if (toRead.Count == 0)
        {
            return null;
        }

        var rows = StoredRowReader.Read(context, table, conditions, [.. toRead.Select(p => p.Column!)]);
        if (rows.Count > 1)
        {
            // Never quote the key: it is a token, and the message may reach the caller's logs.
            throw new QueryRejectedException(
                $"A {entry.Metadata.DisplayName()} being saved has a masked key, and the value its token stands for is " +
                $"stored in {rows.Count} rows under different spellings, so Masque can't tell which row was loaded. " +
                "Nothing was saved. Merge the duplicate rows, or save through a key that isn't masked.");
        }

        if (rows.Count == 0)
        {
            return null;
        }

        var stored = new Dictionary<IProperty, object?>(toRead.Count);
        for (var i = 0; i < toRead.Count; i++)
        {
            stored[toRead[i].Property] = rows[0][i];
        }

        foreach (var (property, token) in tokenKeys)
        {
            if (stored.GetValueOrDefault(property) is string real)
            {
                realKeys[token] = real;
            }
        }

        return stored;
    }

    /// <summary>
    /// Puts the real values in place, records the real keys for the command interceptor, and lets the context's
    /// statements through the database's guard until the save ends.
    /// </summary>
    private void Begin(DbContext context, SavePlan plan)
    {
        End(context);
        var window = sessions.For(context.Database.GetDbConnection()).AllowWrites();
        _saves.Add(context, new SaveInProgress(window, plan.Swaps));
        sessions.Keys.Begin(context, plan.RealKeys);

        foreach (var swap in plan.Swaps)
        {
            swap.Property.CurrentValue = swap.RealValue;
        }
    }

    /// <summary>
    /// Ends the context's save, if one is in progress: puts each token back in its entity, forgets the real keys and
    /// closes the write window. After a successful save the property is marked unchanged again; after a failure it
    /// stays marked for saving, and the next save swaps it again.
    /// </summary>
    private void End(DbContext? context)
    {
        if (context is null || !_saves.TryGetValue(context, out var save))
        {
            return;
        }

        _saves.Remove(context);
        sessions.Keys.End(context);
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

    /// <summary>What a save will change before it runs.</summary>
    /// <param name="Swaps">The tokens to swap for real values in the entities.</param>
    /// <param name="RealKeys">Each masked key token and the key its row really stores.</param>
    private sealed record SavePlan(IReadOnlyList<TokenSwap> Swaps, IReadOnlyDictionary<string, string> RealKeys);

    /// <summary>What a running save must undo when it ends.</summary>
    /// <param name="WriteWindow">Closes the connection's write window.</param>
    /// <param name="Swaps">The tokens to put back.</param>
    private sealed record SaveInProgress(IDisposable WriteWindow, IReadOnlyList<TokenSwap> Swaps);
}
