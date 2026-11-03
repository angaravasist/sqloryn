using System.Collections;
using System.Data;
using System.Data.Common;
using Sqloryn.Core;

/// <summary>
/// In-memory transaction: records calls and replays scripted affected counts / failures.
/// No SQL Server involved; models the contract of one owned SqlTransaction.
/// </summary>
sealed class FakeEditTransactionFactory : IEditTransactionFactory
{
    readonly Queue<object> _script;
    public List<string> Log { get; } = [];
    public Exception? BeginFails { get; init; }
    public Exception? CommitFails { get; init; }
    public Exception? RollbackFails { get; init; }
    public Exception? DisposeFails { get; init; }
    public bool Disposed { get; private set; }

    /// <summary>Each item is an int (affected rows) or an Exception thrown by that command.</summary>
    public FakeEditTransactionFactory(params object[] script) => _script = new Queue<object>(script);

    public Task<IEditTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        if (BeginFails is not null)
            throw BeginFails;
        Log.Add("begin");
        return Task.FromResult<IEditTransaction>(new Tx(this));
    }

    sealed class Tx(FakeEditTransactionFactory owner) : IEditTransaction
    {
        public Task<int> ExecuteAffectedAsync(EditCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.Log.Add("exec");
            var next = owner._script.Dequeue();
            if (next is Exception ex)
                throw ex;
            return Task.FromResult((int)next);
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            if (owner.CommitFails is not null)
                throw owner.CommitFails;
            owner.Log.Add("commit");
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            owner.Log.Add("rollback");
            if (owner.RollbackFails is not null)
                throw owner.RollbackFails;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            owner.Disposed = true;
            owner.Log.Add("dispose");
            if (owner.DisposeFails is not null)
                throw owner.DisposeFails;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Reader with several result sets (for example trigger output followed by our row-count set).</summary>
sealed class MultiResultReader : DbDataReader
{
    readonly (string[] Columns, object?[][] Rows)[] _sets;
    int _set;
    int _row = -1;

    public MultiResultReader(params (string[] Columns, object?[][] Rows)[] sets) => _sets = sets;

    public override int FieldCount => _sets[_set].Columns.Length;
    public override bool HasRows => _sets[_set].Rows.Length > 0;
    public override bool IsClosed => false;
    public override int RecordsAffected => 0;
    public override int Depth => 0;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => ++_row < _sets[_set].Rows.Length;
    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    public override bool NextResult()
    {
        if (_set + 1 >= _sets.Length)
            return false;
        _set++;
        _row = -1;
        return true;
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextResult());
    }

    public override string GetName(int ordinal) => _sets[_set].Columns[ordinal];
    public override int GetOrdinal(string name) => Array.IndexOf(_sets[_set].Columns, name);
    public override Type GetFieldType(int ordinal) => typeof(object);
    public override string GetDataTypeName(int ordinal) => "int";
    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is null or DBNull;
    public override object GetValue(int ordinal) => _sets[_set].Rows[_row][ordinal] ?? DBNull.Value;
    public override int GetValues(object[] values)
    {
        for (var i = 0; i < FieldCount; i++)
            values[i] = GetValue(i);
        return FieldCount;
    }

    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
    public override void Close() { }
    public override DataTable GetSchemaTable() => throw new NotSupportedException();
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
    public override byte GetByte(int ordinal) => throw new NotSupportedException();
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override char GetChar(int ordinal) => throw new NotSupportedException();
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
    public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
    public override double GetDouble(int ordinal) => throw new NotSupportedException();
    public override float GetFloat(int ordinal) => throw new NotSupportedException();
    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
    public override short GetInt16(int ordinal) => throw new NotSupportedException();
    public override long GetInt64(int ordinal) => throw new NotSupportedException();
    public override string GetString(int ordinal) => throw new NotSupportedException();
}

/// <summary>
/// Sequential-access-shaped reader for the QueryRaw byte budget. Records how much data was actually served via
/// GetChars/GetBytes and forbids unbounded GetValue/GetString on large (string/binary) columns.
/// </summary>
sealed class BudgetFakeReader : DbDataReader
{
    readonly string[] _columns;
    readonly string[] _types;
    readonly object?[][] _rows;
    int _row = -1;
    int _lastOrdinal;

    public long CharsServed { get; private set; }
    public long BytesServed { get; private set; }
    public int UnboundedLargeReads { get; private set; }
    public int RowsRead => _row + 1;
    public List<int> OrdinalsTouched { get; } = [];

    public BudgetFakeReader(string[] columns, string[] types, params object?[][] rows)
    {
        _columns = columns;
        _types = types;
        _rows = rows;
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
        _lastOrdinal = -1;
        return ++_row < _rows.Length;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    public override bool NextResult() => false;
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public override string GetName(int ordinal) => _columns[ordinal];
    public override int GetOrdinal(string name) => Array.IndexOf(_columns, name);
    public override Type GetFieldType(int ordinal) => typeof(object);
    public override string GetDataTypeName(int ordinal) => _types[ordinal];

    void Touch(int ordinal)
    {
        if (ordinal < _lastOrdinal)
            throw new InvalidOperationException("sequential access violated");
        _lastOrdinal = ordinal;
        OrdinalsTouched.Add(ordinal);
    }

    public override bool IsDBNull(int ordinal) => _rows[_row][ordinal] is null or DBNull;

    public override object GetValue(int ordinal)
    {
        Touch(ordinal);
        var value = _rows[_row][ordinal];
        if (value is string or byte[])
            UnboundedLargeReads++;
        return value ?? DBNull.Value;
    }

    public override int GetValues(object[] values) => throw new NotSupportedException();
    public override string GetString(int ordinal)
    {
        UnboundedLargeReads++;
        return (string)GetValue(ordinal);
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        Touch(ordinal);
        var s = (string)_rows[_row][ordinal]!;
        if (buffer is null)
            return s.Length;
        var take = (int)Math.Max(0, Math.Min(length, s.Length - dataOffset));
        s.CopyTo((int)dataOffset, buffer, bufferOffset, take);
        CharsServed += take;
        return take;
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        Touch(ordinal);
        var b = (byte[])_rows[_row][ordinal]!;
        if (buffer is null)
            return b.Length;
        var take = (int)Math.Max(0, Math.Min(length, b.Length - dataOffset));
        Array.Copy(b, (int)dataOffset, buffer, bufferOffset, take);
        BytesServed += take;
        return take;
    }

    public override void Close() { }
    public override DataTable GetSchemaTable() => throw new NotSupportedException();
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
    public override byte GetByte(int ordinal) => throw new NotSupportedException();
    public override char GetChar(int ordinal) => throw new NotSupportedException();
    public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
    public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
    public override double GetDouble(int ordinal) => throw new NotSupportedException();
    public override float GetFloat(int ordinal) => throw new NotSupportedException();
    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
    public override short GetInt16(int ordinal) => throw new NotSupportedException();
    public override int GetInt32(int ordinal) => throw new NotSupportedException();
    public override long GetInt64(int ordinal) => throw new NotSupportedException();
}
