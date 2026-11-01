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

/// <summary>DbDataReader that yields one complete string row, then throws on the next Read.</summary>
sealed class OneRowThenThrowReader : DbDataReader
{
    readonly string[] _columns;
    readonly string[] _cells;
    int _state; // 0=before first, 1=on first, 2=throw

    public OneRowThenThrowReader(string[] columns, string[] cells)
    {
        _columns = columns;
        _cells = cells;
    }

    public override int FieldCount => _columns.Length;
    public override bool HasRows => true;
    public override bool IsClosed => false;
    public override int RecordsAffected => 0;
    public override int Depth => 0;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (_state == 0)
        {
            _state = 1;
            return true;
        }
        throw new InvalidOperationException("synthetic mid-result failure");
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        => Task.FromResult(Read());

    public override string GetName(int ordinal) => _columns[ordinal];
    public override int GetOrdinal(string name) => Array.IndexOf(_columns, name);
    public override Type GetFieldType(int ordinal) => typeof(string);
    public override string GetDataTypeName(int ordinal) => "nvarchar";
    public override bool IsDBNull(int ordinal) => false;
    public override object GetValue(int ordinal) => _cells[ordinal];
    public override int GetValues(object[] values)
    {
        for (var i = 0; i < _cells.Length; i++)
            values[i] = _cells[i];
        return _cells.Length;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var s = _cells[ordinal];
        if (buffer is null)
            return s.Length;
        var take = Math.Min(length, s.Length - (int)dataOffset);
        s.CopyTo((int)dataOffset, buffer, bufferOffset, take);
        return take;
    }

    public override bool NextResult() => false;
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
    public override string GetString(int ordinal) => _cells[ordinal];
}
