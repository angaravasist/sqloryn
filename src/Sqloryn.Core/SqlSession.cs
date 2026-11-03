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

    /// <summary>
    /// Same server and credentials as this connected session, targeting <paramref name="database"/>.
    /// In-memory only; used to open a sibling tab session. Null when not connected.
    /// </summary>
    public ConnectionOptions? CreateSiblingOptions(string database)
    {
        var o = IsConnected ? _options : null;
        if (o is null)
            return null;
        return new ConnectionOptions
        {
            Server = o.Server,
            Port = o.Port,
            UserId = o.UserId,
            Password = o.Password,
            Database = database,
            Encrypt = o.Encrypt,
            TrustServerCertificate = o.TrustServerCertificate,
            ServerCertificatePath = o.ServerCertificatePath,
            ConnectTimeoutSeconds = o.ConnectTimeoutSeconds,
            CommandTimeoutSeconds = o.CommandTimeoutSeconds
        };
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

    public Task<QueryResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        => ExecuteAsync(sql, null, cancellationToken);

    /// <summary>
    /// Executes <paramref name="sql"/> (the whole document or a selection of it). When it is a selection, pass
    /// <paramref name="origin"/> so a <see cref="SqlScriptRejectedException"/> reports document line/offset.
    /// </summary>
    public async Task<QueryResult> ExecuteAsync(
        string sql, ScriptExecutionOrigin? origin, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL text is required.");

        // Preflight the ENTIRE selection before batch 1 can run. Unsupported SQLCMD directives, malformed GO
        // lines and out-of-bounds repeat counts throw SqlScriptRejectedException with line/offset; nothing executes.
        SqlScript script;
        try
        {
            script = GoBatchSplitter.Parse(sql);
        }
        catch (SqlScriptRejectedException ex) when (origin is { } o)
        {
            throw ex.WithOrigin(o);
        }
        if (!script.HasSeparator)
            return await ExecuteBatchAsync(sql, null, cancellationToken).ConfigureAwait(false);

        var batches = new List<string>(script.ExpandedBatchCount);
        foreach (var batch in script.Batches)
            for (var r = 0; r < batch.RepeatCount; r++)
                batches.Add(batch.Text);
        if (batches.Count == 0)
            throw new ArgumentException("SQL text is required.");
        if (batches.Count == 1)
            return await ExecuteBatchAsync(batches[0], null, cancellationToken).ConfigureAwait(false);

        var cancelGenAtStart = Volatile.Read(ref _cancelGeneration);
        var aggregate = new BatchAggregate();
        for (var i = 0; i < batches.Count; i++)
        {
            if (i > 0)
            {
                var stop = PreBatchStop(cancellationToken, cancelGenAtStart);
                if (stop is not null)
                {
                    aggregate.StopBeforeBatch(stop.Value, i + 1, batches.Count, Database);
                    break;
                }
            }

            QueryResult result;
            try
            {
                result = await ExecuteBatchAsync(batches[i], i == 0 ? null : cancelGenAtStart, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (i > 0)
            {
                aggregate.FailBeforeBatch(SanitizeMessage(ex.Message), i + 1, batches.Count, Database);
                break;
            }

            aggregate.Add(result, i + 1, batches.Count);
            if (result.Completion != QueryCompletionKind.Succeeded ||
                result.TargetConfidence == TargetConfidence.Unknown)
            {
                aggregate.NoteSkipped(i + 1, batches.Count);
                break;
            }
        }

        return aggregate.Build(sql, ServerDisplay ?? "", Database ?? "");
    }

    StopCause? PreBatchStop(CancellationToken cancellationToken, int cancelGenAtStart)
    {
        if (_disconnectRequested || !IsConnected)
            return StopCause.Disconnect;
        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _cancelGeneration) != cancelGenAtStart)
            return StopCause.UserCancel;
        return null;
    }

    /// <summary>Test seam: replaces provider execution of one batch. Never set in production code.</summary>
    internal Func<string, int?, CancellationToken, Task<QueryResult>>? BatchExecutorForTests { get; init; }

    Task<QueryResult> ExecuteBatchAsync(string sql, int? cancelGenAtStartOverride, CancellationToken cancellationToken)
        => BatchExecutorForTests is { } hook
            ? hook(sql, cancelGenAtStartOverride, cancellationToken)
            : ExecuteBatchCoreAsync(sql, cancelGenAtStartOverride, cancellationToken);

    async Task<QueryResult> ExecuteBatchCoreAsync(string sql, int? cancelGenAtStartOverride, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SqlCommand? command = null;
        CancellationTokenSource? linked = null;
        CancellationTokenSource? watchdogCts = null;
        CancellationTokenSource? watchdogSignal = null;
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

            var timers = new TimerStopEvidence();
            // Soft wall-clock helper only; provider CommandTimeout / error -2 is authoritative for timeouts.
            // Record watchdog cause before cancelling the operation token (no CancelAfter on the
            // reader-facing linked token — attribution must be visible before cancellation reaches it).
            watchdogSignal = new CancellationTokenSource();
            watchdogCts = AttributedTimer.Arm(
                TimeSpan.FromSeconds(Math.Max(_options!.CommandTimeoutSeconds + 5, 10)),
                timers.SignalWatchdog,
                watchdogSignal);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchdogSignal.Token);
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
            var cancelGenAtStart = cancelGenAtStartOverride ?? Volatile.Read(ref _cancelGeneration);
            var obs = new BatchObserver.Observation();
            var sqlPhaseComplete = false;
            var drainDeadline = TimeSpan.FromSeconds(Math.Clamp(_options.CommandTimeoutSeconds, 5, 60));

            StopContext ResolveStop() => new(
                DisconnectRequested: _disconnectRequested,
                UserCancelRequested: cancellationToken.IsCancellationRequested ||
                                     Volatile.Read(ref _cancelGeneration) != cancelGenAtStart,
                WatchdogCancelRequested: timers.WatchdogFired,
                DrainDeadlineRequested: timers.DrainDeadlineFired,
                Timers: timers);

            try
            {
                DbDataReader? reader = null;
                try
                {
                    try
                    {
                        reader = await command.ExecuteReaderAsync(
                            System.Data.CommandBehavior.SequentialAccess, linked.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is SqlException or OperationCanceledException or ProviderErrorException)
                    {
                        BatchObserver.ApplyStop(obs, ex, ResolveStop());
                        if (obs.Completion == QueryCompletionKind.UnknownAfterDisconnect)
                            await DisposeConnectionAsync().ConfigureAwait(false);
                        sqlPhaseComplete = true;
                        goto AfterObserve;
                    }

                    // Drain deadline starts inside Observe when retention stops — not before.
                    obs = await BatchObserver.ObserveOpenedReaderAsync(
                        reader, budget, linked.Token, drainDeadline, ResolveStop, timers).ConfigureAwait(false);
                    sqlPhaseComplete = true;
                }
                finally
                {
                    // Dispose the active reader before same-session verification / cancel probe.
                    if (reader is not null)
                        await reader.DisposeAsync().ConfigureAwait(false);
                }

                // Only refine an already-recorded UserCancel; never overwrite provider/watchdog timeout.
                if (obs.StopCause == StopCause.UserCancel)
                {
                    var verified = await TryProbeAfterCancelAsync().ConfigureAwait(false);
                    if (BatchObserver.TryRefineUserCancel(
                            obs, verified.Verified, verified.TransactionState, out var xact))
                        transactionState = xact;
                }

                if (obs.Completion == QueryCompletionKind.UnknownAfterDisconnect)
                    await DisposeConnectionAsync().ConfigureAwait(false);
            }
            finally
            {
                if (infoConnection is not null && onInfo is not null)
                    infoConnection.InfoMessage -= onInfo;
            }

            AfterObserve:
            foreach (var msg in messages)
                obs.Messages.Insert(0, msg);

            // Separate target verification from SQL completion. Never replay; never upgrade Completion.
            var sqlCompletion = obs.Completion;
            var sqlStop = obs.StopCause;
            var targetConfidence = TargetConfidence.Known;
            if (obs.Completion == QueryCompletionKind.UnknownAfterDisconnect || _connection is null)
            {
                _targetUnknown = true;
                targetConfidence = TargetConfidence.Unknown;
            }
            else if (sqlPhaseComplete)
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

            // Target checks must not change SQL completion / stop cause.
            obs.Completion = sqlCompletion;
            obs.StopCause = sqlStop;

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
            watchdogSignal?.Dispose();
            watchdogCts?.Dispose();
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
                RetentionLimitReached = sqlOutcome.RetentionLimitReached,
                StopCause = sqlOutcome.StopCause
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
            RetentionLimitReached = sqlOutcome.RetentionLimitReached,
            StopCause = sqlOutcome.StopCause
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

    /// <summary>
    /// Runs one parameterised statement on this session's connection and returns the first result set with raw values.
    /// Used by catalog browsing, design and keyed-edit features. Parameter names must be of the form @name.
    /// Rows beyond <paramref name="maxRows"/> are dropped and <see cref="RawRows.Truncated"/> is set.
    /// Reads are sequential and bounded by <paramref name="budget"/> (default <see cref="RawReadBudget.Default"/>):
    /// a string/binary cell over the budget is returned as <see cref="RawCellOmitted"/>, never as a shortened value.
    /// </summary>
    public async Task<RawRows> QueryRawAsync(
        string sql,
        IEnumerable<EditParameter>? parameters = null,
        int maxRows = 5_000,
        CancellationToken cancellationToken = default,
        RawReadBudget? budget = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL text is required.");
        if (maxRows < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRows));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options!.CommandTimeoutSeconds;
            AddParameters(cmd, parameters);

            await using var reader = await cmd.ExecuteReaderAsync(
                System.Data.CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            return await RawRowReader.ReadAsync(reader, maxRows, budget ?? RawReadBudget.Default, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    static void AddParameters(SqlCommand cmd, IEnumerable<EditParameter>? parameters)
    {
        if (parameters is null)
            return;
        foreach (var p in parameters)
        {
            if (string.IsNullOrEmpty(p.Name) || p.Name[0] != '@')
                throw new ArgumentException("Parameter names must start with '@'.");
            if (RawCellOmitted.IsOmitted(p.Value))
                throw new ArgumentException($"Parameter {p.Name} holds a value that was not fully loaded and cannot be written back.");
            cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        }
    }

    /// <summary>
    /// Applies <paramref name="commands"/> (from <see cref="EditRowsSql.BuildUpdate"/>/<see cref="EditRowsSql.BuildDelete"/>)
    /// in ONE owned SqlTransaction. Every write's row count is validated before commit; any conflict, wrong count or
    /// error rolls the whole set back. Refuses to run when the session already has an open transaction.
    /// If the outcome is <see cref="EditBatchOutcome.OutcomeUnknown"/> the connection is closed so no transaction or lock survives.
    /// </summary>
    public async Task<EditBatchResult> ApplyEditsAsync(
        IReadOnlyList<EditCommand> commands, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
            throw new ArgumentException("No edits to apply.", nameof(commands));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? linked = null;
        try
        {
            EnsureConnected();
            if (_targetUnknown)
                throw new InvalidOperationException(
                    "Database target is unknown after a prior batch. Revalidate or reconnect before saving edits.");
            if (Volatile.Read(ref _inFlight) > 0)
                throw new InvalidOperationException("A command is already running on this session.");

            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCts = linked;
            Interlocked.Increment(ref _inFlight);

            var connection = _connection!;
            var timeout = _options!.CommandTimeoutSeconds;

            await using (var state = connection.CreateCommand())
            {
                state.CommandText = "SELECT CONVERT(int, XACT_STATE());";
                state.CommandTimeout = timeout;
                var xact = (int)(await state.ExecuteScalarAsync(linked.Token).ConfigureAwait(false) ?? 0);
                if (xact != 0)
                    return new EditBatchResult(EditBatchOutcome.RolledBackError, 0, null,
                        "A transaction is already open on this session; refusing to save edits inside it. Nothing was written.");
            }

            var factory = new SqlEditTransactionFactory(this, connection, timeout);
            var result = await EditRowsBatch.ApplyAsync(factory, commands, linked.Token).ConfigureAwait(false);
            if (result.Outcome == EditBatchOutcome.OutcomeUnknown)
            {
                // Closing the connection ends any transaction the server may still hold for us.
                _targetUnknown = true;
                await DisposeConnectionAsync().ConfigureAwait(false);
                result = result with { Message = result.Message + " The session was disconnected; reconnect and reload." };
            }
            return result;
        }
        finally
        {
            if (ReferenceEquals(_activeCts, linked))
                _activeCts = null;
            _activeSqlCommand = null;
            linked?.Dispose();
            if (linked is not null)
                Interlocked.Decrement(ref _inFlight);
            _gate.Release();
        }
    }

    sealed class SqlEditTransactionFactory(SqlSession owner, SqlConnection connection, int commandTimeoutSeconds)
        : IEditTransactionFactory
    {
        public async Task<IEditTransaction> BeginAsync(CancellationToken cancellationToken)
        {
            var tx = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            return new SqlEditTransaction(owner, connection, tx, commandTimeoutSeconds);
        }
    }

    sealed class SqlEditTransaction(SqlSession owner, SqlConnection connection, SqlTransaction transaction, int commandTimeoutSeconds)
        : IEditTransaction
    {
        public async Task<int> ExecuteAffectedAsync(EditCommand command, CancellationToken cancellationToken)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = command.CommandText;
            cmd.CommandTimeout = commandTimeoutSeconds;
            cmd.CommandType = CommandType.Text;
            AddParameters(cmd, command.Parameters);
            owner._activeSqlCommand = cmd;
            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                return await EditRowsSql.ReadAffectedCountAsync(reader, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                owner._activeSqlCommand = null;
            }
        }

        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);
        public Task RollbackAsync(CancellationToken cancellationToken) => transaction.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
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

    internal static bool LooksLikeGoBatch(string sql) => GoBatchSplitter.ContainsSeparator(sql);

    // Compatibility shim for existing tests that called SqlSession.FormatCell.
    internal static CellValue FormatCell(System.Data.Common.DbDataReader reader, int ordinal)
        => ResultMaterializer.FormatCell(reader, ordinal);
}

/// <summary>
/// Raw first result set from <see cref="SqlSession.QueryRawAsync"/>. A null value is SQL NULL.
/// A <see cref="RawCellOmitted"/> value marks a cell that was too large or unsupported to load; it must never be written back.
/// </summary>
/// <param name="Truncated">The row cap or the byte budget stopped reading before the result set ended.</param>
/// <param name="ByteBudgetExhausted">The total byte budget (not just the row cap) ended the read.</param>
/// <param name="HasOmittedCells">At least one cell is a <see cref="RawCellOmitted"/> placeholder.</param>
public sealed record RawRows(
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> SqlTypes,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    bool ByteBudgetExhausted = false,
    bool HasOmittedCells = false);
