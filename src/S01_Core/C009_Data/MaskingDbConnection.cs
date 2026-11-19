using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Masque.Core.Policy;

namespace Masque.Data;

/// <summary>
/// A connection whose commands are checked by Masque and whose results come back masked. It decorates (wraps and
/// passes through to) a provider's connection, so code written for ADO.NET, Dapper or EF Core works unchanged.
/// Create it with <see cref="MasqueGate.Wrap"/>; it owns the raw connection and disposes it.
/// </summary>
public sealed class MaskingDbConnection : DbConnection
{
    private readonly DbConnection _inner;
    private MaskingDbTransaction? _transaction;

    internal MaskingDbConnection(MasqueGate gate, DbConnection inner, MasqueSession session, AccessGrant grant)
    {
        Gate = gate;
        _inner = inner;
        Session = session;
        Grant = grant;

        if (inner.State == ConnectionState.Open)
        {
            Secure();
        }
    }

    /// <summary>What the caller may see raw and whether they may write, after the connection's own narrowing.</summary>
    public AccessGrant Grant { get; }

    /// <summary>The gate that wrapped this connection.</summary>
    internal MasqueGate Gate { get; }

    /// <summary>What Masque remembers about this connection.</summary>
    internal MasqueSession Session { get; }

    /// <summary>The raw connection.</summary>
    internal DbConnection Inner => _inner;

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value;
    }

    /// <inheritdoc />
    public override string Database => _inner.Database;

    /// <inheritdoc />
    public override string DataSource => _inner.DataSource;

    /// <inheritdoc />
    public override string ServerVersion => _inner.ServerVersion;

    /// <inheritdoc />
    public override ConnectionState State => _inner.State;

    /// <summary>Not supported: switching databases would skip the check that a query's tables are listed.</summary>
    /// <param name="databaseName">Ignored.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override void ChangeDatabase(string databaseName) =>
        throw new NotSupportedException("Masque doesn't switch databases on an open connection. Open a new connection to the other database.");

    /// <inheritdoc />
    public override void Open()
    {
        _inner.Open();
        Secure();
    }

    /// <inheritdoc />
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        await _inner.OpenAsync(cancellationToken).ConfigureAwait(false);
        Secure();
    }

    /// <inheritdoc />
    public override void Close()
    {
        _inner.Close();
        Session.Reset();
        _transaction = null;
    }

    /// <summary>Forgets a transaction once it has finished, so Masque's own commands stop joining it.</summary>
    internal void TransactionEnded(MaskingDbTransaction transaction)
    {
        if (ReferenceEquals(_transaction, transaction))
        {
            _transaction = null;
            Session.Transaction = null;
        }
    }

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        _transaction = new MaskingDbTransaction(this, _inner.BeginTransaction(isolationLevel));
        Session.Transaction = _transaction.Inner;
        return _transaction;
    }

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => new MaskingDbCommand(this, _inner.CreateCommand());

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            Session.Reset();
        }

        base.Dispose(disposing);
    }

    /// <summary>Adds the dialect's guards each time the raw connection opens (a reopened connection may be a new handle).</summary>
    private void Secure() => Session.Dialect.Secure(_inner, () => Session.WritesAllowed);
}
