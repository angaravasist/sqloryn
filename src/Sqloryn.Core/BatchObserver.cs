using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Sqloryn.Core;

/// <summary>
/// Test stand-in for provider errors when constructing <see cref="SqlException"/> is impractical.
/// Classified identically to SqlException numbers.
/// </summary>
public sealed class ProviderErrorException(int number, string message) : Exception(message)
{
    public int Number { get; } = number;
}

/// <summary>
/// Separates retained display output from command observation/completion.
/// Never replays SQL. Classifies timeout and transport loss at every read boundary.
/// </summary>
public static class BatchObserver
{
    public sealed class Observation
    {
        public List<ResultSet> RetainedSets { get; } = [];
        public QueryCompletionKind Completion { get; set; } = QueryCompletionKind.Succeeded;
        public bool PartialResults { get; set; }
        public bool RetentionLimitReached { get; set; }
        public bool FinalCompletionUnobserved { get; set; }
        public string? ErrorSummary { get; set; }
        public List<QueryMessage> Messages { get; } = [];
    }

    public static async Task DrainResultSetAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (!reader.IsDBNull(i))
                    _ = reader.GetValue(i);
            }
        }
    }

    public static QueryCompletionKind ClassifyFailure(
        Exception ex,
        bool disconnectRequested,
        bool userCancellation)
    {
        if (ex is OperationCanceledException)
            return ExecutionOutcomes.FromCancellation(disconnectRequested, userCancellation, serverStateVerified: false);

        if (ex is SqlException sql)
            return ExecutionOutcomes.FromSqlError(sql.Number);

        if (ex is ProviderErrorException provider)
            return ExecutionOutcomes.FromSqlError(provider.Number);

        return QueryCompletionKind.Failed;
    }

    public static string SummarizeFailure(Exception ex, QueryCompletionKind kind, int retainedRowCount)
    {
        var retained = retainedRowCount > 0
            ? $" Retained {retainedRowCount} complete row(s)."
            : "";
        var number = ex is SqlException s ? s.Number
            : ex is ProviderErrorException p ? p.Number
            : 0;
        return kind switch
        {
            QueryCompletionKind.TimedOut when number == -2 =>
                $"Provider timeout (SQL -2): {Sanitize(ex.Message)}.{retained} SQL was not replayed.",
            QueryCompletionKind.TimedOut =>
                $"Command timed out or observation deadline elapsed.{retained} SQL was not replayed.",
            QueryCompletionKind.UnknownAfterDisconnect when number != 0 =>
                $"Connection lost during execution (SQL {number}); outcome unknown.{retained} SQL was not replayed.",
            QueryCompletionKind.UnknownAfterDisconnect =>
                $"Connection lost during execution; outcome unknown.{retained} SQL was not replayed.",
            QueryCompletionKind.CancelledClientEnded or QueryCompletionKind.CancelledServerVerified =>
                $"Cancellation stopped observation.{retained} SQL was not replayed.",
            QueryCompletionKind.Failed when number != 0 =>
                $"SQL error {number}: {Sanitize(ex.Message)}.{retained} SQL was not replayed.",
            _ => $"{Sanitize(ex.Message)}.{retained} SQL was not replayed."
        };
    }

    /// <summary>
    /// Production observation loop over an already-opened reader.
    /// When retention limits hit, continues draining under <paramref name="drainToken"/> without retaining.
    /// </summary>
    public static async Task<Observation> ObserveOpenedReaderAsync(
        DbDataReader reader,
        ResultBudget budget,
        CancellationToken retainToken,
        CancellationToken drainToken)
    {
        var obs = new Observation();
        var retaining = true;

        try
        {
            while (true)
            {
                if (retaining && (obs.RetainedSets.Count >= QueryLimits.MaxResultSets || budget.Exceeded))
                {
                    obs.RetentionLimitReached = true;
                    obs.PartialResults = true;
                    retaining = false;
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
                    }

                    if (outcome.RowFailure is not null)
                    {
                        var userCancel = outcome.RowFailure is OperationCanceledException &&
                                         retainToken.IsCancellationRequested;
                        // Reader-thrown OCE after retained rows still preserves those rows.
                        if (outcome.RowFailure is OperationCanceledException)
                            userCancel = true; // treat mid-batch cancel as user/client cancel unless drain deadline
                        ApplyStop(obs, outcome.RowFailure, disconnectRequested: false, userCancellation: userCancel);
                        return obs;
                    }

                    if (!retaining)
                        await DrainResultSetAsync(reader, drainToken).ConfigureAwait(false);
                }
                else
                {
                    await DrainResultSetAsync(reader, drainToken).ConfigureAwait(false);
                }

                var more = await reader.NextResultAsync(retaining ? retainToken : drainToken)
                    .ConfigureAwait(false);
                if (!more)
                    break;
            }

            if (obs.RetentionLimitReached)
                obs.PartialResults = true;
            obs.Completion = QueryCompletionKind.Succeeded;
            return obs;
        }
        catch (Exception ex)
        {
            // Drain-deadline cancellation after a retention limit → unobserved final completion.
            if (obs.RetentionLimitReached &&
                ex is OperationCanceledException &&
                drainToken.IsCancellationRequested &&
                !retainToken.IsCancellationRequested)
            {
                obs.FinalCompletionUnobserved = true;
                obs.PartialResults = true;
                obs.Completion = QueryCompletionKind.Succeeded;
                obs.ErrorSummary =
                    "Display retention limit reached; final batch completion was unobserved within the drain deadline. SQL was not replayed.";
                return obs;
            }

            var userCancel = retainToken.IsCancellationRequested && ex is OperationCanceledException;
            ApplyStop(obs, ex, disconnectRequested: false, userCancellation: userCancel);
            return obs;
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
            PartialResults = obs.PartialResults || obs.RetentionLimitReached || obs.FinalCompletionUnobserved,
            TransactionState = transactionState,
            TargetConfidence = targetConfidence,
            FinalCompletionUnobserved = obs.FinalCompletionUnobserved,
            RetentionLimitReached = obs.RetentionLimitReached
        };
    }

    public static void ApplyStop(
        Observation obs,
        Exception ex,
        bool disconnectRequested,
        bool userCancellation)
    {
        var kind = ClassifyFailure(ex, disconnectRequested, userCancellation);
        obs.Completion = kind;
        var rows = obs.RetainedSets.Sum(s => s.Rows.Count);
        obs.PartialResults = rows > 0 || obs.RetentionLimitReached;
        obs.ErrorSummary = SummarizeFailure(ex, kind, rows);
        var number = ex is SqlException s ? s.Number : ex is ProviderErrorException p ? p.Number : 0;
        obs.Messages.Add(new QueryMessage(16, number, Sanitize(ex.Message)));
    }

    static string Sanitize(string message)
    {
        var trimmed = message.Replace('\r', ' ').Replace('\n', ' ');
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }
}
