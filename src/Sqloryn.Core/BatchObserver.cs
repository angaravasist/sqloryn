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

/// <summary>Signals available when an OperationCanceledException is observed.</summary>
public readonly record struct StopContext(
    bool DisconnectRequested,
    bool UserCancelRequested,
    bool WatchdogCancelRequested,
    bool DrainDeadlineRequested);

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
            // Drain deadline only when the drain CTS fired without a competing user cancel.
            if (context.DrainDeadlineRequested && !context.UserCancelRequested)
                return StopCause.DrainDeadline;
            if (context.UserCancelRequested)
                return StopCause.UserCancel;
            if (context.WatchdogCancelRequested)
                return StopCause.WatchdogTimeout;
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
        StopContext stopContext)
        => ObserveOpenedReaderAsync(reader, budget, retainToken, drainDeadline, () => stopContext);

    public static async Task<Observation> ObserveOpenedReaderAsync(
        DbDataReader reader,
        ResultBudget budget,
        CancellationToken retainToken,
        TimeSpan drainDeadline,
        Func<StopContext> resolveStopContext)
    {
        var obs = new Observation();
        var retaining = true;
        CancellationTokenSource? drainCts = null;

        try
        {
            while (true)
            {
                if (retaining && (obs.RetainedSets.Count >= QueryLimits.MaxResultSets || budget.Exceeded))
                {
                    obs.RetentionLimitReached = true;
                    obs.PartialResults = true;
                    retaining = false;
                    drainCts = BeginDrainDeadline(retainToken, drainDeadline);
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
                        drainCts?.Dispose();
                        drainCts = BeginDrainDeadline(retainToken, drainDeadline);
                    }

                    if (outcome.RowFailure is not null)
                    {
                        ApplyStop(obs, outcome.RowFailure, resolveStopContext());
                        return obs;
                    }

                    if (!retaining)
                        await DrainResultSetAsync(reader, drainCts!.Token).ConfigureAwait(false);
                }
                else
                {
                    await DrainResultSetAsync(reader, drainCts!.Token).ConfigureAwait(false);
                }

                var more = await reader.NextResultAsync(retaining ? retainToken : drainCts!.Token)
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
            var ctx = resolveStopContext();
            if (!retaining && drainCts is { IsCancellationRequested: true })
                ctx = ctx with { DrainDeadlineRequested = true };
            ApplyStop(obs, ex, ctx);
            return obs;
        }
        finally
        {
            drainCts?.Dispose();
        }
    }

    static CancellationTokenSource BeginDrainDeadline(CancellationToken userToken, TimeSpan drainDeadline)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(userToken);
        cts.CancelAfter(drainDeadline);
        return cts;
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
            StopCause = obs.StopCause
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

    static string Sanitize(string message)
    {
        var trimmed = message.Replace('\r', ' ').Replace('\n', ' ');
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }
}
