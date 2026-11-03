using System.Collections;
using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using Sqloryn.Core;

/// <summary>Production-shaped SqlDecimal source without a live SqlDataReader.</summary>
sealed class FakeSqlTypedCellSource : ISqlTypedCellSource
{
    readonly string[] _typeNames;
    readonly object?[] _values;

    public FakeSqlTypedCellSource(string[] typeNames, object?[] values)
    {
        _typeNames = typeNames;
        _values = values;
        FieldCount = values.Length;
    }

    public int FieldCount { get; }
    public string GetName(int ordinal) => $"c{ordinal}";
    public bool IsDBNull(int ordinal) => _values[ordinal] is null or DBNull;
    public string GetDataTypeName(int ordinal) => _typeNames[ordinal];

    public SqlDecimal GetSqlDecimal(int ordinal)
    {
        return _values[ordinal] switch
        {
            SqlDecimal sd => sd,
            string s => SqlDecimal.Parse(s),
            _ => throw new InvalidCastException("Not SqlDecimal")
        };
    }

    public DateTimeOffset GetDateTimeOffset(int ordinal) => (DateTimeOffset)_values[ordinal]!;
    public long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var s = (string)_values[ordinal]!;
        if (buffer is null)
            return s.Length;
        var take = Math.Min(length, s.Length - (int)dataOffset);
        s.CopyTo((int)dataOffset, buffer, bufferOffset, take);
        return take;
    }

    public long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var b = (byte[])_values[ordinal]!;
        if (buffer is null)
            return b.Length;
        var take = Math.Min(length, b.Length - (int)dataOffset);
        Array.Copy(b, (int)dataOffset, buffer, bufferOffset, take);
        return take;
    }

    public bool TryGetBoolean(int ordinal, out bool value)
    {
        if (_values[ordinal] is bool b)
        {
            value = b;
            return true;
        }
        value = default;
        return false;
    }

    public bool TryGetDateTime(int ordinal, out DateTime value)
    {
        value = default;
        return false;
    }

    public bool TryGetFormattableNumber(int ordinal, out string text)
    {
        text = "";
        return false;
    }
}

/// <summary>
/// Scripted reader for production BatchObserver path: yields rows, then optional failure on Read/NextResult.
/// </summary>
class ScriptedResultReader : DbDataReader
{
    readonly string[] _columns;
    readonly string[][] _rows;
    readonly Exception? _failOnReadAfterRows;
    readonly Exception? _failOnNextResult;
    int _rowIndex = -1;

    public ScriptedResultReader(
        string[] columns,
        string[][] rows,
        Exception? failOnReadAfterRows = null,
        Exception? failOnNextResult = null)
    {
        _columns = columns;
        _rows = rows;
        _failOnReadAfterRows = failOnReadAfterRows;
        _failOnNextResult = failOnNextResult;
    }

    public override int FieldCount => _columns.Length;
    public override bool HasRows => _rows.Length > 0;
    public override bool IsClosed => false;
    public override int RecordsAffected => 0;
    public override int Depth => 0;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (_rowIndex + 1 < _rows.Length)
        {
            _rowIndex++;
            return true;
        }

        if (_failOnReadAfterRows is not null)
            throw _failOnReadAfterRows;
        return false;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    public override bool NextResult()
    {
        if (_failOnNextResult is not null)
            throw _failOnNextResult;
        return false;
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_failOnNextResult is not null)
            throw _failOnNextResult;
        return Task.FromResult(false);
    }

    public override string GetName(int ordinal) => _columns[ordinal];
    public override int GetOrdinal(string name) => Array.IndexOf(_columns, name);
    public override Type GetFieldType(int ordinal) => typeof(string);
    public override string GetDataTypeName(int ordinal) => "nvarchar";
    public override bool IsDBNull(int ordinal) => false;
    public override object GetValue(int ordinal) => _rows[_rowIndex][ordinal];
    public override int GetValues(object[] values)
    {
        for (var i = 0; i < FieldCount; i++)
            values[i] = GetValue(i);
        return FieldCount;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var s = _rows[_rowIndex][ordinal];
        if (buffer is null)
            return s.Length;
        var take = Math.Min(length, s.Length - (int)dataOffset);
        s.CopyTo((int)dataOffset, buffer, bufferOffset, take);
        return take;
    }

    public override void Close() { }
    public override DataTable GetSchemaTable() => throw new NotSupportedException();
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
    public override byte GetByte(int ordinal) => throw new NotSupportedException();
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();
    public override char GetChar(int ordinal) => throw new NotSupportedException();
    public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
    public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
    public override double GetDouble(int ordinal) => throw new NotSupportedException();
    public override float GetFloat(int ordinal) => throw new NotSupportedException();
    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
    public override short GetInt16(int ordinal) => throw new NotSupportedException();
    public override int GetInt32(int ordinal) => throw new NotSupportedException();
    public override long GetInt64(int ordinal) => throw new NotSupportedException();
    public override string GetString(int ordinal) => _rows[_rowIndex][ordinal];
}

/// <summary>Backward-compatible alias used by older tests.</summary>
sealed class OneRowThenThrowReader : ScriptedResultReader
{
    public OneRowThenThrowReader(string[] columns, string[] cells)
        : base(columns, [cells], failOnReadAfterRows: new InvalidOperationException("synthetic mid-result failure"))
    {
    }
}

/// <summary>
/// Allows GetValue only on the first <paramref name="retainableRows"/> rows.
/// Discarded-row access throws; after all rows, NextResult raises a SQL error.
/// </summary>
sealed class DiscardValueGuardReader : DbDataReader
{
    readonly string[] _columns;
    readonly string[][] _rows;
    readonly int _retainableRows;
    readonly Exception _lateError;
    int _rowIndex = -1;
    public int DiscardedValueAccessAttempts { get; private set; }

    public DiscardValueGuardReader(string[] columns, string[][] rows, int retainableRows, Exception lateError)
    {
        _columns = columns;
        _rows = rows;
        _retainableRows = retainableRows;
        _lateError = lateError;
    }

    public override int FieldCount => _columns.Length;
    public override bool HasRows => _rows.Length > 0;
    public override bool IsClosed => false;
    public override int RecordsAffected => 0;
    public override int Depth => 0;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (_rowIndex + 1 < _rows.Length)
        {
            _rowIndex++;
            return true;
        }
        return false;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    public override bool NextResult() => throw _lateError;

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw _lateError;
    }

    public override string GetName(int ordinal) => _columns[ordinal];
    public override int GetOrdinal(string name) => Array.IndexOf(_columns, name);
    public override Type GetFieldType(int ordinal) => typeof(string);
    public override string GetDataTypeName(int ordinal) => "nvarchar";
    public override bool IsDBNull(int ordinal) => false;

    public override object GetValue(int ordinal)
    {
        if (_rowIndex >= _retainableRows)
        {
            DiscardedValueAccessAttempts++;
            throw new InvalidOperationException("discarded-value access rejected");
        }
        return _rows[_rowIndex][ordinal];
    }

    public override int GetValues(object[] values)
    {
        for (var i = 0; i < FieldCount; i++)
            values[i] = GetValue(i);
        return FieldCount;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        _ = GetValue(ordinal);
        var s = _rows[_rowIndex][ordinal];
        if (buffer is null)
            return s.Length;
        var take = Math.Min(length, s.Length - (int)dataOffset);
        s.CopyTo((int)dataOffset, buffer, bufferOffset, take);
        return take;
    }

    public override void Close() { }
    public override DataTable GetSchemaTable() => throw new NotSupportedException();
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
    public override byte GetByte(int ordinal) => throw new NotSupportedException();
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();
    public override char GetChar(int ordinal) => throw new NotSupportedException();
    public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
    public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
    public override double GetDouble(int ordinal) => throw new NotSupportedException();
    public override float GetFloat(int ordinal) => throw new NotSupportedException();
    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
    public override short GetInt16(int ordinal) => throw new NotSupportedException();
    public override int GetInt32(int ordinal) => throw new NotSupportedException();
    public override long GetInt64(int ordinal) => throw new NotSupportedException();
    public override string GetString(int ordinal) => (string)GetValue(ordinal);
}

/// <summary>
/// Row 1 retained; row 2 triggers retention stop; further Reads hang until the drain token cancels.
/// </summary>
sealed class HangAfterFirstRowReader : DbDataReader
{
    readonly string[] _columns;
    readonly string[] _firstRow;
    int _reads;

    public HangAfterFirstRowReader(string[] columns, string[] firstRow)
    {
        _columns = columns;
        _firstRow = firstRow;
    }

    public override int FieldCount => _columns.Length;
    public override bool HasRows => true;
    public override bool IsClosed => false;
    public override int RecordsAffected => 0;
    public override int Depth => 0;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => throw new NotSupportedException("Use ReadAsync");

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        _reads++;
        if (_reads <= 2)
            return true; // 1 = retained, 2 = overflow that stops retention
        // Drain path: wait until drain deadline cancels.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            await tcs.Task.ConfigureAwait(false);
        return false;
    }

    public override bool NextResult() => false;
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public override string GetName(int ordinal) => _columns[ordinal];
    public override int GetOrdinal(string name) => Array.IndexOf(_columns, name);
    public override Type GetFieldType(int ordinal) => typeof(string);
    public override string GetDataTypeName(int ordinal) => "nvarchar";
    public override bool IsDBNull(int ordinal) => false;
    public override object GetValue(int ordinal)
        => _reads <= 1 ? _firstRow[ordinal] : $"overflow-{ordinal}";
    public override int GetValues(object[] values)
    {
        for (var i = 0; i < FieldCount; i++)
            values[i] = GetValue(i);
        return FieldCount;
    }
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var s = (string)GetValue(ordinal);
        if (buffer is null)
            return s.Length;
        var take = Math.Min(length, s.Length - (int)dataOffset);
        s.CopyTo((int)dataOffset, buffer, bufferOffset, take);
        return take;
    }
    public override void Close() { }
    public override DataTable GetSchemaTable() => throw new NotSupportedException();
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
    public override byte GetByte(int ordinal) => throw new NotSupportedException();
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();
    public override char GetChar(int ordinal) => throw new NotSupportedException();
    public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
    public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
    public override double GetDouble(int ordinal) => throw new NotSupportedException();
    public override float GetFloat(int ordinal) => throw new NotSupportedException();
    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
    public override short GetInt16(int ordinal) => throw new NotSupportedException();
    public override int GetInt32(int ordinal) => throw new NotSupportedException();
    public override long GetInt64(int ordinal) => throw new NotSupportedException();
    public override string GetString(int ordinal) => (string)GetValue(ordinal);
}
