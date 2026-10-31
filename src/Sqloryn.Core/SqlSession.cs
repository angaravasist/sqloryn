using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Sqloryn.Core;

/// <summary>Owns one SQL connection for a workspace tab. No pooling, no automatic replay.</summary>
public sealed class SqlSession : IAsyncDisposable
{
    readonly SemaphoreSlim _gate = new(1, 1);
    SqlConnection? _connection;
    ConnectionOptions? _options;
    CancellationTokenSource? _activeCts;
    SqlCommand? _activeSqlCommand;
    int _inFlight;
    bool _disposed;

    public bool IsConnected => _connection is { State: ConnectionState.Open };
    public string? ServerDisplay { get; private set; }
    public string? Database { get; private set; }
    public string? ServerVersion { get; private set; }
    public bool HasActiveCommand => Volatile.Read(ref _inFlight) > 0;

    public async Task ConnectAsync(ConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasActiveCommand)
                throw new InvalidOperationException("Cannot reconnect while a command is running.");
            await DisposeConnectionAsync().ConfigureAwait(false);
            var connection = new SqlConnection(options.BuildConnectionString());
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), DB_NAME();";
                cmd.CommandTimeout = options.CommandTimeoutSeconds;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Server identity query returned no rows.");
                ServerVersion = reader.GetString(0);
                Database = reader.IsDBNull(1) ? options.Database : reader.GetString(1);
                ServerDisplay = options.DisplayTarget;
                _options = options;
                _connection = connection;
                connection = null;
            }
            finally
            {
                if (connection is not null)
                    await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ChangeDatabaseAsync(string database, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(database))
            throw new ArgumentException("Database is required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            if (HasActiveCommand)
                throw new InvalidOperationException("Cannot change database while a command is running.");
            // Refuse silent discard of an open transaction.
            await using (var state = _connection!.CreateCommand())
            {
                state.CommandText = "SELECT CONVERT(int, XACT_STATE());";
                state.CommandTimeout = _options!.CommandTimeoutSeconds;
                var xact = (int)(await state.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0);
                if (xact != 0)
                    throw new InvalidOperationException("Refusing database change while a transaction is open.");
            }
            await _connection.ChangeDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
            Database = database;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelInFlight_NoThrow();
            await DisposeConnectionAsync().ConfigureAwait(false);
            ServerDisplay = null;
            Database = null;
            ServerVersion = null;
            _options = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void RequestCancel() => CancelInFlight_NoThrow();

    public async Task<QueryResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL text is required.");
        // Preview contract: single batch only; no GO splitting.
        if (LooksLikeGoBatch(sql))
            throw new InvalidOperationException("GO batch separators are not supported in Developer Preview 0. Run a single batch.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SqlCommand? command = null;
        CancellationTokenSource? linked = null;
        try
        {
            EnsureConnected();
            if (HasActiveCommand)
                throw new InvalidOperationException("A command is already running on this session.");

            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(_options!.CommandTimeoutSeconds));
            _activeCts = linked;
            Interlocked.Increment(ref _inFlight);

            command = _connection!.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _options.CommandTimeoutSeconds;
            command.CommandType = CommandType.Text;
            _activeSqlCommand = command;

            var messages = new List<QueryMessage>();
            void OnInfo(object sender, SqlInfoMessageEventArgs e)
            {
                foreach (SqlError error in e.Errors)
                {
                    if (messages.Count >= QueryLimits.MaxMessages)
                        break;
                    messages.Add(new QueryMessage(error.Class, error.Number, SanitizeMessage(error.Message)));
                }
            }
            _connection.InfoMessage += OnInfo;
            var sets = new List<ResultSet>();
            string? errorSummary = null;
            var completion = QueryCompletionKind.Succeeded;
            var partial = false;
            try
            {
                await using var reader = await command.ExecuteReaderAsync(linked.Token).ConfigureAwait(false);
                do
                {
                    if (sets.Count >= QueryLimits.MaxResultSets)
                    {
                        partial = true;
                        break;
                    }
                    sets.Add(await ReadResultSetAsync(reader, linked.Token).ConfigureAwait(false));
                    if (sets[^1].Truncated)
                        partial = true;
                } while (await reader.NextResultAsync(linked.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion = QueryCompletionKind.CancelledConfirmed;
                partial = sets.Count > 0;
                errorSummary = "Cancellation requested; awaiting confirmation that the batch stopped.";
            }
            catch (OperationCanceledException)
            {
                completion = QueryCompletionKind.TimedOut;
                partial = sets.Count > 0;
                errorSummary = "Command timed out.";
            }
            catch (SqlException ex) when (IsBrokenConnection(ex))
            {
                completion = QueryCompletionKind.UnknownAfterDisconnect;
                partial = sets.Count > 0;
                errorSummary = $"Connection lost during execution (SQL {ex.Number}); outcome unknown; SQL was not replayed.";
                await DisposeConnectionAsync().ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                completion = QueryCompletionKind.Failed;
                partial = sets.Count > 0;
                errorSummary = $"SQL error {ex.Number}: {SanitizeMessage(ex.Message)}";
                messages.Add(new QueryMessage(ex.Class, ex.Number, SanitizeMessage(ex.Message)));
            }
            finally
            {
                _connection.InfoMessage -= OnInfo;
            }

            return new QueryResult
            {
                ExecutedSql = sql,
                Server = ServerDisplay ?? "",
                Database = Database ?? "",
                Completion = completion,
                ResultSets = sets,
                Messages = messages,
                ErrorSummary = errorSummary,
                PartialResults = partial
            };
        }
        finally
        {
            if (ReferenceEquals(_activeSqlCommand, command))
                _activeSqlCommand = null;
            if (command is not null)
                await command.DisposeAsync().ConfigureAwait(false);
            if (ReferenceEquals(_activeCts, linked))
                _activeCts = null;
            linked?.Dispose();
            Interlocked.Decrement(ref _inFlight);
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT name FROM sys.databases
            WHERE HAS_DBACCESS(name) = 1
            ORDER BY name;
            """;
        return await ReadNameListAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListSchemasAsync(string database, CancellationToken cancellationToken = default)
    {
        await ChangeDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT name FROM sys.schemas
            WHERE schema_id < 16384
            ORDER BY name;
            """;
        return await ReadNameListAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(string Schema, string Name, string Type)>> ListTablesAndViewsAsync(
        string database, string schema, CancellationToken cancellationToken = default)
    {
        await ChangeDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        var sql = $"""
            SELECT s.name, o.name, o.type
            FROM sys.objects AS o
            INNER JOIN sys.schemas AS s ON o.schema_id = s.schema_id
            WHERE o.type IN ('U', 'V') AND s.name = @schema
            ORDER BY o.type, o.name;
            """;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.CommandTimeout = _options!.CommandTimeoutSeconds;
            var list = new List<(string, string, string)>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2).Trim()));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<(string Name, string TypeName, bool IsNullable)>> ListColumnsAsync(
        string database, string schema, string table, CancellationToken cancellationToken = default)
    {
        await ChangeDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT c.name, t.name, c.is_nullable
            FROM sys.columns AS c
            INNER JOIN sys.types AS t ON c.user_type_id = t.user_type_id
            INNER JOIN sys.objects AS o ON c.object_id = o.object_id
            INNER JOIN sys.schemas AS s ON o.schema_id = s.schema_id
            WHERE s.name = @schema AND o.name = @table
            ORDER BY c.column_id;
            """;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", table);
            cmd.CommandTimeout = _options!.CommandTimeoutSeconds;
            var list = new List<(string, string, bool)>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    void EnsureConnected()
    {
        if (_connection is not { State: ConnectionState.Open } || _options is null)
            throw new InvalidOperationException("Not connected.");
    }

    void CancelInFlight_NoThrow()
    {
        try { _activeCts?.Cancel(); } catch { /* ignore */ }
        try { _activeSqlCommand?.Cancel(); } catch { /* ignore */ }
    }

    async Task DisposeConnectionAsync()
    {
        if (_connection is null)
            return;
        var connection = _connection;
        _connection = null;
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        catch { /* ignore dispose failures */ }
    }

    async Task<IReadOnlyList<string>> ReadNameListAsync(string sql, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options!.CommandTimeoutSeconds;
            var list = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(reader.GetString(0));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    static async Task<ResultSet> ReadResultSetAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        var columns = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            columns[i] = string.IsNullOrEmpty(reader.GetName(i)) ? $"Column{i + 1}" : reader.GetName(i);

        var rows = new List<IReadOnlyList<CellValue>>();
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count >= QueryLimits.MaxRowsPerSet)
            {
                truncated = true;
                break;
            }
            var cells = new CellValue[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                cells[i] = FormatCell(reader, i);
            rows.Add(cells);
        }
        return new ResultSet { Columns = columns, Rows = rows, Truncated = truncated };
    }

    internal static CellValue FormatCell(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return CellValue.Null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            string s => CellValue.Text(Truncate(s), s.Length == 0 ? "empty-string" : "string"),
            char c => CellValue.Text(c.ToString(), "string"),
            byte[] bytes => CellValue.Text("0x" + Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, QueryLimits.MaxCellChars / 2))), "binary"),
            decimal d => CellValue.Text(d.ToString(CultureInfo.InvariantCulture), "decimal"),
            DateTimeOffset dto => CellValue.Text(dto.ToString("o", CultureInfo.InvariantCulture), "datetimeoffset"),
            DateTime dt => CellValue.Text(dt.ToString("o", CultureInfo.InvariantCulture), "datetime"),
            bool b => CellValue.Text(b ? "true" : "false", "bool"),
            IFormattable formattable => CellValue.Text(Truncate(formattable.ToString(null, CultureInfo.InvariantCulture) ?? ""), "number"),
            _ => CellValue.Text(Truncate(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""), "other")
        };
    }

    static string Truncate(string value)
        => value.Length <= QueryLimits.MaxCellChars
            ? value
            : value[..QueryLimits.MaxCellChars] + "…";

    static string SanitizeMessage(string message)
    {
        var trimmed = message.Replace('\r', ' ').Replace('\n', ' ');
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }

    static bool IsBrokenConnection(SqlException ex)
        => ex.Number is -1 or -2 or 233 or 10053 or 10054 or 64;

    /// <summary>Conservative detection only — preview does not implement GO batching.</summary>
    internal static bool LooksLikeGoBatch(string sql)
    {
        using var reader = new StringReader(sql);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("GO ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("GO\t", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
