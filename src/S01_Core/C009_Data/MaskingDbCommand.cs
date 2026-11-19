using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Masque.Core;
using Masque.Core.Query;

namespace Masque.Data;

/// <summary>
/// A command on a <see cref="MaskingDbConnection"/>. Before it runs, Masque checks the statement against the caller's
/// access; its readers and scalars come back masked. Parameters pass through to the database unchanged.
/// </summary>
public sealed class MaskingDbCommand : DbCommand
{
    private readonly DbCommand _inner;
    private MaskingDbConnection? _connection;
    private MaskingDbTransaction? _transaction;
    private string _commandText = string.Empty;

    internal MaskingDbCommand(MaskingDbConnection connection, DbCommand inner)
    {
        _connection = connection;
        _inner = inner;
    }

    /// <summary>
    /// Raw types to allow for this command only; narrows the connection's access and never widens it. Null (the
    /// default) keeps the connection's access.
    /// </summary>
    public IReadOnlyCollection<SensitiveDataType>? AllowedRawTypes { get; set; }

    /// <inheritdoc />
    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set
        {
            _commandText = value ?? string.Empty;
            _inner.CommandText = _commandText;
        }
    }

    /// <inheritdoc />
    public override int CommandTimeout
    {
        get => _inner.CommandTimeout;
        set => _inner.CommandTimeout = value;
    }

    /// <inheritdoc />
    public override CommandType CommandType { get; set; } = CommandType.Text;

    /// <inheritdoc />
    public override bool DesignTimeVisible
    {
        get => _inner.DesignTimeVisible;
        set => _inner.DesignTimeVisible = value;
    }

    /// <inheritdoc />
    public override UpdateRowSource UpdatedRowSource
    {
        get => _inner.UpdatedRowSource;
        set => _inner.UpdatedRowSource = value;
    }

    /// <inheritdoc />
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            _connection = value switch
            {
                null => null,
                MaskingDbConnection masking => masking,
                _ => throw new InvalidOperationException("A Masque command runs only on a Masque connection; wrap the connection first."),
            };
            _inner.Connection = _connection?.Inner;
        }
    }

    /// <inheritdoc />
    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

    /// <inheritdoc />
    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            _transaction = value switch
            {
                null => null,
                MaskingDbTransaction masking => masking,
                _ => throw new InvalidOperationException("A Masque command takes only a transaction from its Masque connection."),
            };
            _inner.Transaction = _transaction?.Inner;
        }
    }

    /// <inheritdoc />
    public override void Cancel() => _inner.Cancel();

    /// <summary>Does nothing: the statement is checked, and may be rewritten, each time it runs.</summary>
    public override void Prepare()
    {
    }

    /// <inheritdoc />
    public override int ExecuteNonQuery()
    {
        var (prepared, connection) = Begin();
        using (prepared)
        {
            try
            {
                return _inner.ExecuteNonQuery();
            }
            catch (DbException ex)
            {
                throw connection.Session.Dialect.Describe(ex);
            }
        }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var (prepared, connection) = Begin();
        using (prepared)
        {
            try
            {
                return await _inner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbException ex)
            {
                throw connection.Session.Dialect.Describe(ex);
            }
        }
    }

    /// <summary>Runs the command and returns the first column of the first row, masked like any reader value.</summary>
    /// <returns>The value, or <see langword="null"/> when there are no rows.</returns>
    public override object? ExecuteScalar()
    {
        using var reader = ExecuteDbDataReader(CommandBehavior.Default);
        return reader.Read() && reader.FieldCount > 0 ? reader.GetValue(0) : null;
    }

    /// <inheritdoc />
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.FieldCount > 0 ? reader.GetValue(0) : null;
    }

    /// <inheritdoc />
    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

    /// <inheritdoc />
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var (prepared, connection) = Begin();
        DbDataReader reader;
        try
        {
            reader = _inner.ExecuteReader(behavior | connection.Session.Dialect.LineageBehavior);
        }
        catch (DbException ex)
        {
            prepared.Dispose();
            throw connection.Session.Dialect.Describe(ex);
        }
        catch
        {
            prepared.Dispose();
            throw;
        }

        prepared.EndWrites();
        return connection.Gate.WrapReader(reader, prepared);
    }

    /// <inheritdoc />
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var (prepared, connection) = Begin();
        DbDataReader reader;
        try
        {
            reader = await _inner.ExecuteReaderAsync(behavior | connection.Session.Dialect.LineageBehavior, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            prepared.Dispose();
            throw connection.Session.Dialect.Describe(ex);
        }
        catch
        {
            prepared.Dispose();
            throw;
        }

        prepared.EndWrites();
        return connection.Gate.WrapReader(reader, prepared);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Checks the statement and readies the raw command, joining the connection's current transaction.</summary>
    private (PreparedCommand Prepared, MaskingDbConnection Connection) Begin()
    {
        var connection = _connection ?? throw new InvalidOperationException("The command has no connection.");
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection must be open before a command runs.");
        }

        // Masque's own commands (sampling tables) must use the same transaction the database expects.
        connection.Session.Transaction = _transaction?.Inner ?? connection.Session.Transaction;
        _inner.Transaction = _transaction?.Inner ?? connection.Session.Transaction;

        var grant = MasqueGate.GrantFor(connection.Grant, AllowedRawTypes, _commandText);
        return (connection.Gate.Prepare(_inner, _commandText, CommandType, connection.Session, grant), connection);
    }
}
