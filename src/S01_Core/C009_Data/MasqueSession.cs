using System.Data.Common;
using Masque.Core.Config;
using Masque.Core.Query;
using Masque.Data.Dialects;

namespace Masque.Data;

/// <summary>
/// What Masque remembers about one wrapped connection: its dialect, its current transaction, the tables it lists,
/// and the reviewed configs it has loaded. How each token's value is spelled in the data is kept in the vault, not
/// here, so token filters work on any connection.
/// </summary>
/// <param name="connection">The raw connection.</param>
/// <param name="dialect">The dialect that handles it.</param>
internal sealed class MasqueSession(DbConnection connection, IMasqueDialect dialect)
{
    private readonly Dictionary<string, MaskConfig> _reviewed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private IReadOnlyList<TableReference>? _tables;
    private int _writeDepth;

    /// <summary>The raw connection.</summary>
    public DbConnection Connection { get; } = connection;

    /// <summary>The dialect that handles the connection.</summary>
    public IMasqueDialect Dialect { get; } = dialect;

    /// <summary>
    /// The raw connection's current transaction. Masque's own commands must join it (Microsoft.Data.Sqlite refuses a
    /// command without it).
    /// </summary>
    public DbTransaction? Transaction { get; set; }

    /// <summary>Whether a checked write is running, which the dialect's guard reads.</summary>
    public bool WritesAllowed => Volatile.Read(ref _writeDepth) > 0;

    /// <summary>Lets writes through the dialect's guard until the returned scope is disposed.</summary>
    /// <returns>The scope.</returns>
    public IDisposable AllowWrites()
    {
        Interlocked.Increment(ref _writeDepth);
        return new WriteScope(this);
    }

    /// <summary>Finds a listed table matching a statement's table name, listing tables on first use.</summary>
    /// <param name="table">A table named in a statement or reported by a reader.</param>
    /// <returns>The listed table, or <see langword="null"/> when the database doesn't list it.</returns>
    public TableReference? FindListed(TableReference table)
    {
        var normalized = Dialect.Normalize(table);
        return ListedTables().FirstOrDefault(t => string.Equals(t.Qualified, normalized.Qualified, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The user tables and views the database lists, read once per open connection.</summary>
    /// <returns>The listed tables.</returns>
    public IReadOnlyList<TableReference> ListedTables() => _tables ??= Dialect.ListTables(Connection, Transaction);

    /// <summary>Gets a reviewed config loaded earlier on this connection.</summary>
    public bool TryGetReviewed(TableReference table, out MaskConfig config)
    {
        lock (_lock)
        {
            return _reviewed.TryGetValue(table.Qualified, out config!);
        }
    }

    /// <summary>Remembers a reviewed config for the rest of this connection's life.</summary>
    public void RememberReviewed(TableReference table, MaskConfig config)
    {
        lock (_lock)
        {
            _reviewed[table.Qualified] = config;
        }
    }

    /// <summary>Forgets the table list, for when the connection is closed and may reopen on another database.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _tables = null;
            _reviewed.Clear();
            Transaction = null;
        }
    }

    /// <summary>Ends one <see cref="AllowWrites"/> scope.</summary>
    private sealed class WriteScope(MasqueSession session) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref session._writeDepth);
            }
        }
    }
}
