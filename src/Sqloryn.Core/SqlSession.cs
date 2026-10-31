using System.Data;
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
    bool _disconnectRequested;
    int _cancelGeneration;

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
        _disconnectRequested = true;
        CancelInFlight_NoThrow();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeConnectionAsync().ConfigureAwait(false);
            ServerDisplay = null;
            Database = null;
            ServerVersion = null;
            _options = null;
        }
        finally
        {
            _disconnectRequested = false;
            _gate.Release();
        }
    }

    public void RequestCancel()
    {
        Interlocked.Increment(ref _cancelGeneration);
        CancelInFlight_NoThrow();
    }

    public async Task<QueryResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL text is required.");
        if (LooksLikeGoBatch(sql))
            throw new InvalidOperationException("GO batch separators are not supported in Developer Preview 0. Run a single batch.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SqlCommand? command = null;
        CancellationTokenSource? linked = null;
        SqlConnection? infoConnection = null;
        SqlInfoMessageEventHandler? onInfo = null;
        try
        {
            EnsureConnected();
            if (Volatile.Read(ref _inFlight) > 0)
                throw new InvalidOperationException("A command is already running on this session. Cancel it or wait; SQL is never queued or replayed.");

            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Soft wall-clock helper only; provider CommandTimeout / error -2 is authoritative for timeouts.
            linked.CancelAfter(TimeSpan.FromSeconds(Math.Max(_options!.CommandTimeoutSeconds + 5, 10)));
            _activeCts = linked;
            Interlocked.Increment(ref _inFlight);

            infoConnection = _connection!;
            command = infoConnection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _options.CommandTimeoutSeconds;
            command.CommandType = CommandType.Text;
            _activeSqlCommand = command;

            var messages = new List<QueryMessage>();
            onInfo = (_, e) =>
            {
                foreach (SqlError error in e.Errors)
                {
                    if (messages.Count >= QueryLimits.MaxMessages)
                        break;
                    messages.Add(new QueryMessage(error.Class, error.Number, SanitizeMessage(error.Message)));
                }
            };
            infoConnection.InfoMessage += onInfo;

            var sets = new List<ResultSet>();
            var budget = new ResultBudget();
            string? errorSummary = null;
            var completion = QueryCompletionKind.Succeeded;
            var partial = false;
            int? transactionState = null;
            var cancelGenAtStart = Volatile.Read(ref _cancelGeneration);

            try
            {
                await using var reader = await command.ExecuteReaderAsync(linked.Token).ConfigureAwait(false);
                do
                {
                    if (sets.Count >= QueryLimits.MaxResultSets || budget.Exceeded)
                    {
                        partial = true;
                        break;
                    }
                    var set = await ResultMaterializer.ReadResultSetAsync(reader, budget, linked.Token).ConfigureAwait(false);
                    sets.Add(set);
                    if (set.Truncated || set.BudgetExceeded)
                        partial = true;
                } while (await reader.NextResultAsync(linked.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_disconnectRequested)
            {
                completion = QueryCompletionKind.UnknownAfterDisconnect;
                partial = sets.Count > 0;
                errorSummary = "Disconnected during execution; server-side outcome unknown; SQL was not replayed.";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested ||
                                                     Volatile.Read(ref _cancelGeneration) != cancelGenAtStart)
            {
                var verified = await TryProbeAfterCancelAsync().ConfigureAwait(false);
                completion = ExecutionOutcomes.FromCancellation(
                    disconnectRequested: false,
                    userCancellation: true,
                    serverStateVerified: verified.Verified);
                transactionState = verified.TransactionState;
                partial = sets.Count > 0;
                errorSummary = verified.Verified
                    ? $"Cancellation: client command ended; server probe OK (XACT_STATE={verified.TransactionState}). Cancel does not imply rollback."
                    : "Cancellation: client command ended; server/transaction state was not verified. SQL was not replayed.";
            }
            catch (OperationCanceledException)
            {
                completion = QueryCompletionKind.TimedOut;
                partial = sets.Count > 0;
                errorSummary = "Command cancelled by client timeout watchdog; SQL was not replayed.";
            }
            catch (SqlException ex)
            {
                completion = ExecutionOutcomes.FromSqlError(ex.Number);
                partial = sets.Count > 0;
                errorSummary = completion switch
                {
                    QueryCompletionKind.TimedOut =>
                        $"Provider timeout (SQL -2): {SanitizeMessage(ex.Message)}. Rows read before timeout are retained; SQL was not replayed.",
                    QueryCompletionKind.UnknownAfterDisconnect =>
                        $"Connection lost during execution (SQL {ex.Number}); outcome unknown; SQL was not replayed.",
                    _ => $"SQL error {ex.Number}: {SanitizeMessage(ex.Message)}"
                };
                messages.Add(new QueryMessage(ex.Class, ex.Number, SanitizeMessage(ex.Message)));
                if (completion == QueryCompletionKind.UnknownAfterDisconnect)
                    await DisposeConnectionAsync().ConfigureAwait(false);
            }
            finally
            {
                if (infoConnection is not null && onInfo is not null)
                    infoConnection.InfoMessage -= onInfo;
            }

            if (completion == QueryCompletionKind.Succeeded)
                await RefreshDatabaseNameNoLockAsync().ConfigureAwait(false);

            return new QueryResult
            {
                ExecutedSql = sql,
                Server = ServerDisplay ?? "",
                Database = Database ?? "",
                Completion = completion,
                ResultSets = sets,
                Messages = messages,
                ErrorSummary = errorSummary,
                PartialResults = partial,
                TransactionState = transactionState
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

    public async Task RefreshDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshDatabaseNameNoLockAsync().ConfigureAwait(false);
        }
        finally
        {
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
        var sql = $"""
            SELECT name FROM {SqlIdentity.QuoteIdentifier(database)}.sys.schemas
            WHERE schema_id < 16384
            ORDER BY name;
            """;
        return await ReadNameListAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(string Schema, string Name, string Type)>> ListTablesAndViewsAsync(
        string database, string schema, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT s.name, o.name, o.type
            FROM {SqlIdentity.QuoteIdentifier(database)}.sys.objects AS o
            INNER JOIN {SqlIdentity.QuoteIdentifier(database)}.sys.schemas AS s ON o.schema_id = s.schema_id
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
        var db = SqlIdentity.QuoteIdentifier(database);
        var sql = $"""
            SELECT c.name, t.name, c.is_nullable
            FROM {db}.sys.columns AS c
            INNER JOIN {db}.sys.types AS t ON c.user_type_id = t.user_type_id
            INNER JOIN {db}.sys.objects AS o ON c.object_id = o.object_id
            INNER JOIN {db}.sys.schemas AS s ON o.schema_id = s.schema_id
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

    async Task RefreshDatabaseNameNoLockAsync()
    {
        if (_connection is not { State: ConnectionState.Open })
            return;
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DB_NAME();";
        cmd.CommandTimeout = _options?.CommandTimeoutSeconds ?? 15;
        var name = await cmd.ExecuteScalarAsync().ConfigureAwait(false) as string;
        if (!string.IsNullOrEmpty(name))
            Database = name;
    }

    async Task<(bool Verified, int? TransactionState)> TryProbeAfterCancelAsync()
    {
        try
        {
            if (_connection is not { State: ConnectionState.Open })
                return (false, null);
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT CONVERT(int, XACT_STATE());";
            cmd.CommandTimeout = 5;
            var xact = (int)(await cmd.ExecuteScalarAsync().ConfigureAwait(false) ?? 0);
            await RefreshDatabaseNameNoLockAsync().ConfigureAwait(false);
            return (true, xact);
        }
        catch
        {
            return (false, null);
        }
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

    static string SanitizeMessage(string message)
    {
        var trimmed = message.Replace('\r', ' ').Replace('\n', ' ');
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }

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

    // Compatibility shim for existing tests that called SqlSession.FormatCell.
    internal static CellValue FormatCell(System.Data.Common.DbDataReader reader, int ordinal)
        => ResultMaterializer.FormatCell(reader, ordinal);
}
