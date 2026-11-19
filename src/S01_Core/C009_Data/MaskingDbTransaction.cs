using System.Data;
using System.Data.Common;

namespace Masque.Data;

/// <summary>A transaction on a <see cref="MaskingDbConnection"/>; passes through to the raw transaction.</summary>
public sealed class MaskingDbTransaction : DbTransaction
{
    private readonly MaskingDbConnection _connection;

    internal MaskingDbTransaction(MaskingDbConnection connection, DbTransaction inner)
    {
        _connection = connection;
        Inner = inner;
    }

    /// <inheritdoc />
    public override IsolationLevel IsolationLevel => Inner.IsolationLevel;

    /// <summary>The raw transaction.</summary>
    internal DbTransaction Inner { get; }

    /// <inheritdoc />
    protected override DbConnection DbConnection => _connection;

    /// <inheritdoc />
    public override void Commit()
    {
        Inner.Commit();
        _connection.TransactionEnded(this);
    }

    /// <inheritdoc />
    public override void Rollback()
    {
        Inner.Rollback();
        _connection.TransactionEnded(this);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.Dispose();
            _connection.TransactionEnded(this);
        }

        base.Dispose(disposing);
    }
}
