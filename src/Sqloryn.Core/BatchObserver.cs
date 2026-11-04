using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Sqloryn.Core;

/// <summary>
/// Test stand-in for provider errors when constructing <see cref="SqlException"/> is impractical.
/// </summary>
public sealed class ProviderErrorException(int number, string message) : Exception(message)
{
    public int Number { get; } = number;
}

/// <summary>
/// Independent evidence of which soft timer expired. A linked token's cancelled state alone
/// cannot attribute Watchdog vs DrainDeadline — the cause is recorded before the operation
/// token is signalled. First timer wins.
/// </summary>
public sealed class TimerStopEvidence
{
    int _captured; // 0 none, 1 watchdog, 2 drain

    public void NoteWatchdog() => Interlocked.CompareExchange(ref _captured, 1, 0);

    public void NoteDrainDeadline() => Interlocked.CompareExchange(ref _captured, 2, 0);

    public bool WatchdogFired => Volatile.Read(ref _captured) == 1;

    public bool DrainDeadlineFired => Volatile.Read(ref _captured) == 2;

    public StopCause? CapturedTimerCause => Volatile.Read(ref _captured) switch
    {
        1 => StopCause.WatchdogTimeout,
        2 => StopCause.DrainDeadline,
        _ => null
    };

    /// <summary>Record watchdog expiry, then cancel the operation token.</summary>
    public void SignalWatchdog(CancellationTokenSource operation)
    {
        NoteWatchdog();
        CancelQuietly(operation);
    }

    /// <summary>Record drain-deadline expiry, then cancel the operation token.</summary>
    public void SignalDrainDeadline(CancellationTokenSource operation)
    {
        NoteDrainDeadline();
        CancelQuietly(operation);
    }

    static void CancelQuietly(CancellationTokenSource operation)
    {
        try
        {
            operation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Operation already torn down; evidence remains.
        }
    }
}

/// <summary>
/// Arms a soft timer that records attribution before signalling the operation CTS.
/// </summary>
public static class AttributedTimer
{
    /// <summary>
    /// When the timer fires, invokes <paramref name="recordThenSignal"/> (must record cause, then
    /// cancel <paramref name="operation"/>). Returns the timer CTS for disposal.
    /// </summary>
    public static CancellationTokenSource Arm(
        TimeSpan delay,
        Action<CancellationTokenSource> recordThenSignal,
        CancellationTokenSource operation)
    {
        var timer = new CancellationTokenSource();
        timer.Token.Register(() => recordThenSignal(operation));
        if (delay <= TimeSpan.Zero)
            timer.Cancel();
        else
            timer.CancelAfter(delay);
        return timer;
    }
}

/// <summary>Signals available when an OperationCanceledException is observed.</summary>
public readonly record struct StopContext(
    bool DisconnectRequested,
    bool UserCancelRequested,
    bool WatchdogCancelRequested,
    bool DrainDeadlineRequested,
    TimerStopEvidence? Timers = null);

/// <summary>
/// Separates retained display output from command observation/completion.
/// Never replays SQL. Preserves stop cause across ExecuteReader / Read / NextResult.
/// </summary>
public static class BatchObserver
{
    public sealed class Observation
    {
        public List<ResultSet> RetainedSets { get; } = [];
        public QueryCompletionKind Completion { get; set; } = QueryCompletionKind.Succeeded;
        public StopCause StopCause { get; set; } = StopCause.None;
        public bool PartialResults { get; set; }
        public bool RetentionLimitReached { get; set; }
        public string? ErrorSummary { get; set; }
        public List<QueryMessage> Messages { get; } = [];
        /// <summary>Provider RecordsAffected when non-negative. Null means the provider did not report a count.</summary>
        public int? RowsAffected { get; set; }
        /// <summary>Client stopwatch around execute, in milliseconds. Not a server statistic.</summary>
        public long? ClientElapsedMilliseconds { get; set; }
    }

    /// <summary>Advance the reader without accessing discarded cell values.</summary>
    public static async Task DrainResultSetAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Intentionally no GetValue / GetChars / IsDBNull — discarded rows are not materialised.
        }
    }

    public static StopCause ClassifyStop(Exception ex, StopContext context)
    {
        if (ex is OperationCanceledException)
        {
            if (context.DisconnectRequested)
                return StopCause.Disconnect;

            // Independent timer evidence wins over linked-token ambiguity.
            if (context.Timers?.CapturedTimerCause is StopCause timed)
                return timed;

            if (context.WatchdogCancelRequested)
                return StopCause.WatchdogTimeout;
            if (context.DrainDeadlineRequested)
                return StopCause.DrainDeadline;
            if (context.UserCancelRequested)
                return StopCause.UserCancel;
            return StopCause.WatchdogTimeout;
        }

        var number = ex is SqlException sql ? sql.Number
            : ex is ProviderErrorException provider ? provider.Number
            : (int?)null;
        if (number is int n)
        {
            if (ExecutionOutcomes.IsProviderTimeout(n))
                return StopCause.ProviderTimeout;
            if (ExecutionOutcomes.IsBrokenConnection(n))
                return StopCause.TransportLoss;
            return StopCause.SqlError;
        }

        return StopCause.Other;
    }

    public static QueryCompletionKind CompletionFor(StopCause cause) => cause switch
    {
        StopCause.None => QueryCompletionKind.Succeeded,
        StopCause.UserCancel => QueryCompletionKind.CancelledClientEnded,
        StopCause.Disconnect => QueryCompletionKind.UnknownAfterDisconnect,
        StopCause.WatchdogTimeout => QueryCompletionKind.TimedOut,
        StopCause.ProviderTimeout => QueryCompletionKind.TimedOut,
        StopCause.DrainDeadline => QueryCompletionKind.FinalCompletionUnobserved,
        StopCause.TransportLoss => QueryCompletionKind.UnknownAfterDisconnect,
        StopCause.SqlError => QueryCompletionKind.Failed,
        _ => QueryCompletionKind.Failed
    };

    public static string SummarizeStop(StopCause cause, Exception? ex, int retainedRowCount)
    {
        var retained = retainedRowCount > 0
            ? $" Retained {retainedRowCount} complete row(s)."
            : "";
        var detail = ex is null ? "" : " " + Sanitize(ex.Message);
        return cause switch
        {
            StopCause.UserCancel =>
                $"Cancellation stopped observation.{retained} SQL was not replayed.",
            StopCause.Disconnect =>
                $"Disconnected during execution; outcome unknown.{retained} SQL was not replayed.",
            StopCause.WatchdogTimeout =>
                $"Watchdog timeout stopped observation.{detail}.{retained} SQL was not replayed.",
            StopCause.ProviderTimeout =>
                $"Provider timeout (SQL -2).{detail}.{retained} SQL was not replayed.",
            StopCause.DrainDeadline =>
                $"Display retention limit reached; final batch completion was unobserved within the drain deadline.{retained} SQL was not replayed.",
            StopCause.TransportLoss =>
                $"Connection lost during execution.{detail}.{retained} SQL was not replayed.",
            StopCause.SqlError =>
                $"SQL error.{detail}.{retained} SQL was not replayed.",
            _ => $"{Sanitize(ex?.Message ?? "Stopped")}.{retained} SQL was not replayed."
        };
    }

    /// <summary>
    /// Production observation loop. Drain deadline starts only when retention stops.
    /// </summary>
    public static Task<Observation> ObserveOpenedReaderAsync(
        DbDataReader reader,
        ResultBudget budget,
        CancellationToken retainToken,
        TimeSpan drainDeadline,
        StopContext stopContext,
        TimerStopEvidence? timers = null)
        => ObserveOpenedReaderAsync(
            reader, budget, retainToken, drainDeadline, () => stopContext, timers ?? stopContext.Timers);

    public static async Task<Observation> ObserveOpenedReaderAsync(
        DbDataReader reader,
        ResultBudget budget,
        CancellationToken retainToken,
        TimeSpan drainDeadline,
        Func<StopContext> resolveStopContext,
        TimerStopEvidence? timers = null)
    {
        // Always keep independent drain/watchdog evidence; linked cancel alone is not attribution.
        timers ??= resolveStopContext().Timers ?? new TimerStopEvidence();
        var obs = new Observation();
        var retaining = true;
        DrainSession? drain = null;

        try
        {
            while (true)
            {
                if (retaining && (obs.RetainedSets.Count >= QueryLimits.MaxResultSets || budget.Exceeded))
                {
                    obs.RetentionLimitReached = true;
                    obs.PartialResults = true;
                    retaining = false;
                    drain = BeginDrainDeadline(retainToken, drainDeadline, timers);
                }

                if (retaining)
                {
                    var outcome = await ResultMaterializer.ReadResultSetAsync(reader, budget, retainToken)
                        .ConfigureAwait(false);
                    obs.RetainedSets.Add(outcome.Set);
                    if (outcome.Set.Truncated || outcome.Set.BudgetExceeded)
                    {
                        obs.PartialResults = true;
                        obs.RetentionLimitReached = true;
                        retaining = false;
                        drain?.Dispose();
                        drain = BeginDrainDeadline(retainToken, drainDeadline, timers);
                    }

                    if (outcome.RowFailure is not null)
                    {
                        ApplyStop(obs, outcome.RowFailure, ResolveAtFailure(resolveStopContext, timers));
                        return obs;
                    }

                    if (!retaining)
                        await DrainResultSetAsync(reader, drain!.Token).ConfigureAwait(false);
                }
                else
                {
                    await DrainResultSetAsync(reader, drain!.Token).ConfigureAwait(false);
                }

                var more = await reader.NextResultAsync(retaining ? retainToken : drain!.Token)
                    .ConfigureAwait(false);
                if (!more)
                    break;
            }

            if (obs.RetentionLimitReached)
                obs.PartialResults = true;
            obs.Completion = QueryCompletionKind.Succeeded;
            obs.StopCause = StopCause.None;
            return obs;
        }
        catch (Exception ex)
        {
            ApplyStop(obs, ex, ResolveAtFailure(resolveStopContext, timers));
            return obs;
        }
        finally
        {
            drain?.Dispose();
        }
    }

    static StopContext ResolveAtFailure(Func<StopContext> resolveStopContext, TimerStopEvidence? timers)
    {
        var ctx = resolveStopContext();
        var evidence = timers ?? ctx.Timers;
        if (evidence is null)
            return ctx;

        // Attribute from callback evidence — never from linked CTS.IsCancellationRequested alone.
        return ctx with
        {
            Timers = evidence,
            WatchdogCancelRequested = evidence.WatchdogFired || ctx.WatchdogCancelRequested,
            DrainDeadlineRequested = evidence.DrainDeadlineFired || ctx.DrainDeadlineRequested
        };
    }

    static DrainSession BeginDrainDeadline(
        CancellationToken retainToken,
        TimeSpan drainDeadline,
        TimerStopEvidence timers)
    {
        // Operation signal is cancelled only after NoteDrainDeadline — never CancelAfter on the
        // reader-facing token directly (that could race attribution past the reader).
        var operation = new CancellationTokenSource();
        var timer = AttributedTimer.Arm(drainDeadline, timers.SignalDrainDeadline, operation);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(retainToken, operation.Token);
        return new DrainSession(timer, operation, linked);
    }

    sealed class DrainSession(
        CancellationTokenSource timer,
        CancellationTokenSource operation,
        CancellationTokenSource linked) : IDisposable
    {
        public CancellationToken Token => linked.Token;

        public void Dispose()
        {
            linked.Dispose();
            operation.Dispose();
            timer.Dispose();
        }
    }

    public static QueryResult BuildResult(
        string sql,
        string server,
        string database,
        Observation obs,
        TargetConfidence targetConfidence = TargetConfidence.Known,
        int? transactionState = null)
    {
        return new QueryResult
        {
            ExecutedSql = sql,
            Server = server,
            Database = database,
            Completion = obs.Completion,
            ResultSets = obs.RetainedSets,
            Messages = obs.Messages,
            ErrorSummary = obs.ErrorSummary,
            PartialResults = obs.PartialResults || obs.RetentionLimitReached ||
                             obs.Completion == QueryCompletionKind.FinalCompletionUnobserved,
            TransactionState = transactionState,
            TargetConfidence = targetConfidence,
            RetentionLimitReached = obs.RetentionLimitReached,
            StopCause = obs.StopCause,
            RowsAffected = obs.RowsAffected,
            ClientElapsedMilliseconds = obs.ClientElapsedMilliseconds
        };
    }

    public static void ApplyStop(Observation obs, Exception ex, StopContext context)
    {
        var cause = ClassifyStop(ex, context);
        obs.StopCause = cause;
        obs.Completion = CompletionFor(cause);
        var rows = obs.RetainedSets.Sum(s => s.Rows.Count);
        obs.PartialResults = rows > 0 || obs.RetentionLimitReached ||
                             cause == StopCause.DrainDeadline;
        obs.ErrorSummary = SummarizeStop(cause, ex, rows);
        var number = ex is SqlException s ? s.Number : ex is ProviderErrorException p ? p.Number : 0;
        obs.Messages.Add(new QueryMessage(16, number, Sanitize(ex.Message)));
    }

    /// <summary>
    /// Same-session cancel probe may refine an already-recorded UserCancel only.
    /// Provider / watchdog timeouts are preserved if the user clicks Cancel later.
    /// </summary>
    public static bool TryRefineUserCancel(
        Observation obs,
        bool serverStateVerified,
        int? transactionState,
        out int? recordedTransactionState)
    {
        recordedTransactionState = null;
        if (obs.StopCause != StopCause.UserCancel)
            return false;

        obs.Completion = ExecutionOutcomes.FromCancellation(
            disconnectRequested: false,
            userCancellation: true,
            serverStateVerified: serverStateVerified);
        recordedTransactionState = transactionState;
        var rows = obs.RetainedSets.Sum(s => s.Rows.Count);
        obs.ErrorSummary = serverStateVerified
            ? $"Cancellation: client command ended; server probe OK (XACT_STATE={transactionState}). Retained {rows} complete row(s). Cancel does not imply rollback."
            : SummarizeStop(StopCause.UserCancel, null, rows);
        return true;
    }

    static string Sanitize(string message)
    {
        var trimmed = message.Replace('\r', ' ').Replace('\n', ' ');
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }
}
