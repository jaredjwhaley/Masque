using System.Collections;
using System.Data;
using System.Data.Common;
using Masque.Data.Dialects;
using Masque.Data.Masking;

namespace Masque.Data;

/// <summary>
/// A reader whose values are masked by plan: tokens replace sensitive values, and <see cref="IsMasked"/> says which
/// columns hold them. Columns the database added for itself are hidden, and only one result set is ever returned.
/// </summary>
/// <remarks>
/// A masked column always reads as text, because a token is never a valid value of the original type: read it with
/// <see cref="GetString"/> or <see cref="GetValue"/>. Typed getters such as <see cref="GetInt32"/> on a masked value
/// throw <see cref="InvalidCastException"/>.
/// </remarks>
public sealed class MaskingDbDataReader : DbDataReader
{
    private readonly DbDataReader _inner;
    private readonly IReadOnlyList<ResultColumn> _columns;
    private readonly IReadOnlyList<ColumnMasking> _plans;
    private readonly ValueMasker _masker;
    private readonly PreparedCommand _prepared;
    private readonly object?[] _row;
    private readonly bool[] _rowMasked;
    private readonly bool[] _rowLoaded;
    private bool _closed;

    internal MaskingDbDataReader(
        DbDataReader inner, IReadOnlyList<ResultColumn> columns, IReadOnlyList<ColumnMasking> plans, ValueMasker masker, PreparedCommand prepared)
    {
        _inner = inner;
        _columns = columns;
        _plans = plans;
        _masker = masker;
        _prepared = prepared;
        _row = new object?[columns.Count];
        _rowMasked = new bool[columns.Count];
        _rowLoaded = new bool[columns.Count];
    }

    /// <inheritdoc />
    public override int Depth => _inner.Depth;

    /// <summary>The number of columns the caller sees (columns the database added for itself are left out).</summary>
    public override int FieldCount => _columns.Count;

    /// <inheritdoc />
    public override bool HasRows => _inner.HasRows;

    /// <inheritdoc />
    public override bool IsClosed => _closed || _inner.IsClosed;

    /// <inheritdoc />
    public override int RecordsAffected => _inner.RecordsAffected;

    /// <inheritdoc />
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc />
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <summary>Whether every value in a column is replaced by a token.</summary>
    /// <param name="ordinal">The column's position.</param>
    /// <returns><see langword="true"/> when the column is masked.</returns>
    public bool IsMasked(int ordinal) => Plan(ordinal).IsMasked;

    /// <summary>How a column is masked and why. Never holds a value.</summary>
    /// <param name="ordinal">The column's position.</param>
    /// <returns>The column's plan.</returns>
    public ColumnMasking GetMasking(int ordinal) => Plan(ordinal);

    /// <inheritdoc />
    public override bool Read()
    {
        try
        {
            return Advance(_inner.Read());
        }
        catch (DbException ex)
        {
            throw _prepared.Session.Dialect.Describe(ex);
        }
    }

    /// <inheritdoc />
    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return Advance(await _inner.ReadAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (DbException ex)
        {
            throw _prepared.Session.Dialect.Describe(ex);
        }
    }

    /// <summary>Always <see langword="false"/>: a checked statement has one result set, and any other is hidden.</summary>
    /// <returns><see langword="false"/>.</returns>
    public override bool NextResult() => false;

    /// <inheritdoc />
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    /// <inheritdoc />
    public override object GetValue(int ordinal) => Value(ordinal, out _);

    /// <inheritdoc />
    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    /// <inheritdoc />
    public override bool IsDBNull(int ordinal) => Value(ordinal, out _) is DBNull;

    /// <inheritdoc />
    public override string GetString(int ordinal)
    {
        var value = Value(ordinal, out var masked);
        return masked ? (string)value : _inner.GetString(_columns[ordinal].Ordinal);
    }

    /// <inheritdoc />
    public override bool GetBoolean(int ordinal) => Typed(ordinal, _inner.GetBoolean);

    /// <inheritdoc />
    public override byte GetByte(int ordinal) => Typed(ordinal, _inner.GetByte);

    /// <inheritdoc />
    public override char GetChar(int ordinal) => Typed(ordinal, _inner.GetChar);

    /// <inheritdoc />
    public override DateTime GetDateTime(int ordinal) => Typed(ordinal, _inner.GetDateTime);

    /// <inheritdoc />
    public override decimal GetDecimal(int ordinal) => Typed(ordinal, _inner.GetDecimal);

    /// <inheritdoc />
    public override double GetDouble(int ordinal) => Typed(ordinal, _inner.GetDouble);

    /// <inheritdoc />
    public override float GetFloat(int ordinal) => Typed(ordinal, _inner.GetFloat);

    /// <inheritdoc />
    public override Guid GetGuid(int ordinal) => Typed(ordinal, _inner.GetGuid);

    /// <inheritdoc />
    public override short GetInt16(int ordinal) => Typed(ordinal, _inner.GetInt16);

    /// <inheritdoc />
    public override int GetInt32(int ordinal) => Typed(ordinal, _inner.GetInt32);

    /// <inheritdoc />
    public override long GetInt64(int ordinal) => Typed(ordinal, _inner.GetInt64);

    /// <inheritdoc />
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        Typed(ordinal, inner => _inner.GetBytes(inner, dataOffset, buffer, bufferOffset, length));

    /// <inheritdoc />
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var value = Value(ordinal, out var masked);
        if (!masked)
        {
            return _inner.GetChars(_columns[ordinal].Ordinal, dataOffset, buffer, bufferOffset, length);
        }

        var token = (string)value;
        if (buffer is null)
        {
            return token.Length;
        }

        var count = (int)Math.Clamp(token.Length - dataOffset, 0, length);
        token.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    /// <inheritdoc />
    public override string GetDataTypeName(int ordinal) =>
        IsMasked(ordinal) ? "text" : _inner.GetDataTypeName(_columns[ordinal].Ordinal);

    /// <summary>The column's type; masked columns are always <see cref="string"/>.</summary>
    /// <param name="ordinal">The column's position.</param>
    /// <returns>The type.</returns>
    public override Type GetFieldType(int ordinal) =>
        IsMasked(ordinal) ? typeof(string) : _inner.GetFieldType(_columns[ordinal].Ordinal);

    /// <inheritdoc />
    public override string GetName(int ordinal) => Column(ordinal).Name;

    /// <inheritdoc />
    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _columns.Count; i++)
        {
            if (string.Equals(_columns[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (var i = 0; i < _columns.Count; i++)
        {
            if (string.Equals(_columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new IndexOutOfRangeException($"The result has no column named {name}.");
    }

    /// <summary>The schema of the visible columns, with masked columns typed as text and source names left out.</summary>
    /// <returns>The schema table.</returns>
    public override DataTable GetSchemaTable()
    {
        var schema = new DataTable("SchemaTable") { Locale = System.Globalization.CultureInfo.InvariantCulture };
        schema.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        schema.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        schema.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));

        for (var i = 0; i < _columns.Count; i++)
        {
            schema.Rows.Add(_columns[i].Name, i, GetFieldType(i), true);
        }

        return schema;
    }

    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    /// <inheritdoc />
    public override void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _inner.Close();
        _prepared.Dispose();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Moves to the next row and forgets the last row's masked values.</summary>
    private bool Advance(bool hasRow)
    {
        Array.Clear(_row);
        Array.Clear(_rowMasked);
        Array.Clear(_rowLoaded);
        return hasRow;
    }

    /// <summary>The current row's value for a column, masked by plan (computed once per row).</summary>
    private object Value(int ordinal, out bool masked)
    {
        var column = Column(ordinal);
        if (!_rowLoaded[ordinal])
        {
            _row[ordinal] = _masker.Mask(_plans[ordinal], _inner.GetValue(column.Ordinal), out _rowMasked[ordinal]);
            _rowLoaded[ordinal] = true;
        }

        masked = _rowMasked[ordinal];
        return _row[ordinal]!;
    }

    /// <summary>Reads a value with a typed getter, which only works on values that weren't replaced by a token.</summary>
    private T Typed<T>(int ordinal, Func<int, T> read)
    {
        Value(ordinal, out var masked);
        return masked
            ? throw new InvalidCastException($"Column {GetName(ordinal)} holds a Masque token here; read it as text.")
            : read(_columns[ordinal].Ordinal);
    }

    private ResultColumn Column(int ordinal) =>
        ordinal >= 0 && ordinal < _columns.Count ? _columns[ordinal] : throw new IndexOutOfRangeException($"No column at position {ordinal}.");

    private ColumnMasking Plan(int ordinal)
    {
        Column(ordinal);
        return _plans[ordinal];
    }
}
