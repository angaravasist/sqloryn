using System.Data;
using System.Data.Common;
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
    bool _targetUnknown;

    public bool IsConnected => _connection is { State: ConnectionState.Open };
    public string? ServerDisplay { get; private set; }
    public string? Database { get; private set; }
    public string? ServerVersion { get; private set; }
    public bool HasActiveCommand => Volatile.Read(ref _inFlight) > 0;
    /// <summary>True when the last batch's target could not be verified; further execution is blocked.</summary>
    public bool TargetUnknown => _targetUnknown;

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
                _targetUnknown = false;
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
            _targetUnknown = false;
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
            if (_targetUnknown)
                throw new InvalidOperationException(
                    "Database target is unknown after a prior batch. Revalidate or reconnect before running SQL. Prior SQL is never replayed.");
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

            var budget = new ResultBudget();
            int? transactionState = null;
            var cancelGenAtStart = Volatile.Read(ref _cancelGeneration);
            var obs = new BatchObserver.Observation();
            var sqlPhaseComplete = false;

            try
            {
                DbDataReader reader;
                try
                {
                    reader = await command.ExecuteReaderAsync(
                        System.Data.CommandBehavior.SequentialAccess, linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SqlException or OperationCanceledException or ProviderErrorException)
                {
                    if (ex is OperationCanceledException && _disconnectRequested)
                        BatchObserver.ApplyStop(obs, ex, disconnectRequested: true, userCancellation: false);
                    else if (ex is OperationCanceledException &&
                             (cancellationToken.IsCancellationRequested ||
                              Volatile.Read(ref _cancelGeneration) != cancelGenAtStart))
                        BatchObserver.ApplyStop(obs, ex, disconnectRequested: false, userCancellation: true);
                    else
                        BatchObserver.ApplyStop(obs, ex, disconnectRequested: false, userCancellation: false);
                    if (obs.Completion == QueryCompletionKind.UnknownAfterDisconnect)
                        await DisposeConnectionAsync().ConfigureAwait(false);
                    sqlPhaseComplete = true;
                    goto AfterObserve;
                }

                await using (reader)
                {
                    using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    // Bounded observation after retention stops; does not replay SQL.
                    drainCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.CommandTimeoutSeconds, 5, 60)));
                    obs = await BatchObserver.ObserveOpenedReaderAsync(
                        reader, budget, linked.Token, drainCts.Token).ConfigureAwait(false);

                    if (obs.Completion is QueryCompletionKind.CancelledClientEnded
                        or QueryCompletionKind.CancelledServerVerified
                        || (obs.Completion == QueryCompletionKind.TimedOut &&
                            (cancellationToken.IsCancellationRequested ||
                             Volatile.Read(ref _cancelGeneration) != cancelGenAtStart)))
                    {
                        // Reclassify user cancel with optional server probe.
                        if (cancellationToken.IsCancellationRequested ||
                            Volatile.Read(ref _cancelGeneration) != cancelGenAtStart)
                        {
                            var verified = await TryProbeAfterCancelAsync().ConfigureAwait(false);
                            obs.Completion = ExecutionOutcomes.FromCancellation(
                                disconnectRequested: false,
                                userCancellation: true,
                                serverStateVerified: verified.Verified);
                            transactionState = verified.TransactionState;
                            var rows = obs.RetainedSets.Sum(s => s.Rows.Count);
                            obs.ErrorSummary = verified.Verified
                                ? $"Cancellation: client command ended; server probe OK (XACT_STATE={verified.TransactionState}). Retained {rows} complete row(s). Cancel does not imply rollback."
                                : BatchObserver.SummarizeFailure(
                                    new OperationCanceledException(),
                                    QueryCompletionKind.CancelledClientEnded,
                                    rows);
                        }
                    }

                    if (obs.Completion == QueryCompletionKind.UnknownAfterDisconnect)
                        await DisposeConnectionAsync().ConfigureAwait(false);

                    sqlPhaseComplete = true;
                }
            }
            finally
            {
                if (infoConnection is not null && onInfo is not null)
                    infoConnection.InfoMessage -= onInfo;
            }

            AfterObserve:
            foreach (var msg in messages)
                obs.Messages.Insert(0, msg);

            // Separate target verification from SQL completion. Never replay the batch.
            var targetConfidence = TargetConfidence.Known;
            if (obs.Completion == QueryCompletionKind.UnknownAfterDisconnect || _connection is null)
            {
                _targetUnknown = true;
                targetConfidence = TargetConfidence.Unknown;
            }
            else if (sqlPhaseComplete || obs.Completion is QueryCompletionKind.Succeeded or QueryCompletionKind.Failed
                     or QueryCompletionKind.TimedOut or QueryCompletionKind.CancelledClientEnded
                     or QueryCompletionKind.CancelledServerVerified)
            {
                var verify = await TryVerifyTargetNoLockAsync().ConfigureAwait(false);
                if (!verify.Ok)
                {
                    _targetUnknown = true;
                    targetConfidence = TargetConfidence.Unknown;
                    var verifyNote = "Target verification failed after the batch; database is unknown until revalidation. SQL was not replayed.";
                    obs.ErrorSummary = string.IsNullOrEmpty(obs.ErrorSummary)
                        ? verifyNote
                        : obs.ErrorSummary + " " + verifyNote;
                }
                else
                {
                    _targetUnknown = false;
                    targetConfidence = TargetConfidence.Known;
                    Database = verify.Database ?? Database;
                }
            }

            return BatchObserver.BuildResult(
                sql,
                ServerDisplay ?? "",
                targetConfidence == TargetConfidence.Unknown ? (Database ?? "(unknown)") : (Database ?? ""),
                obs,
                targetConfidence,
                transactionState);
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
            var verify = await TryVerifyTargetNoLockAsync().ConfigureAwait(false);
            if (!verify.Ok)
                throw new InvalidOperationException("Unable to verify the current database target.");
            _targetUnknown = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Safe revalidation after TargetConfidence.Unknown. Does not replay prior SQL.</summary>
    public async Task RevalidateTargetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            var verify = await TryVerifyTargetNoLockAsync().ConfigureAwait(false);
            if (!verify.Ok)
                throw new InvalidOperationException(
                    "Target revalidation failed. Reconnect before running SQL. Prior batches are never replayed.");
            _targetUnknown = false;
            Database = verify.Database ?? Database;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Pure combine helper for tests: SQL outcome preserved when target verification fails.</summary>
    public static QueryResult WithTargetVerification(QueryResult sqlOutcome, bool verificationSucceeded, string? verifiedDatabase)
    {
        if (verificationSucceeded)
        {
            return new QueryResult
            {
                ExecutedSql = sqlOutcome.ExecutedSql,
                Server = sqlOutcome.Server,
                Database = verifiedDatabase ?? sqlOutcome.Database,
                Completion = sqlOutcome.Completion,
                ResultSets = sqlOutcome.ResultSets,
                Messages = sqlOutcome.Messages,
                ErrorSummary = sqlOutcome.ErrorSummary,
                PartialResults = sqlOutcome.PartialResults,
                TransactionState = sqlOutcome.TransactionState,
                TargetConfidence = TargetConfidence.Known,
                FinalCompletionUnobserved = sqlOutcome.FinalCompletionUnobserved,
                RetentionLimitReached = sqlOutcome.RetentionLimitReached
            };
        }

        var note = "Target verification failed after the batch; database is unknown until revalidation. SQL was not replayed.";
        return new QueryResult
        {
            ExecutedSql = sqlOutcome.ExecutedSql,
            Server = sqlOutcome.Server,
            Database = "(unknown)",
            Completion = sqlOutcome.Completion,
            ResultSets = sqlOutcome.ResultSets,
            Messages = sqlOutcome.Messages,
            ErrorSummary = string.IsNullOrEmpty(sqlOutcome.ErrorSummary)
                ? note
                : sqlOutcome.ErrorSummary + " " + note,
            PartialResults = sqlOutcome.PartialResults,
            TransactionState = sqlOutcome.TransactionState,
            TargetConfidence = TargetConfidence.Unknown,
            FinalCompletionUnobserved = sqlOutcome.FinalCompletionUnobserved,
            RetentionLimitReached = sqlOutcome.RetentionLimitReached
        };
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

    async Task<(bool Ok, string? Database)> TryVerifyTargetNoLockAsync()
    {
        try
        {
            if (_connection is not { State: ConnectionState.Open })
                return (false, null);
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DB_NAME();";
            cmd.CommandTimeout = Math.Min(_options?.CommandTimeoutSeconds ?? 15, 15);
            var name = await cmd.ExecuteScalarAsync().ConfigureAwait(false) as string;
            if (string.IsNullOrEmpty(name))
                return (false, null);
            Database = name;
            return (true, name);
        }
        catch
        {
            return (false, null);
        }
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
            var verify = await TryVerifyTargetNoLockAsync().ConfigureAwait(false);
            return (verify.Ok, xact);
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
