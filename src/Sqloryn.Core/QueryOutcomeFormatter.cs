using System.Globalization;
using System.Text;

namespace Sqloryn.Core;

/// <summary>
/// Single user-visible status mapping shared by the tab status bar, results pane and messages.
/// </summary>
public static class QueryOutcomeFormatter
{
    /// <summary>
    /// Last observed <c>XACT_STATE</c> from a finished batch. This is not a live monitor and it does not
    /// start, commit or roll back a transaction.
    /// </summary>
    public static string DescribeTransaction(int? state) => state switch
    {
        1 => "Transaction: open (XACT_STATE 1), from the last batch",
        0 => "Transaction: none (XACT_STATE 0), from the last batch",
        -1 => "Transaction: uncommittable (XACT_STATE -1), from the last batch",
        null => "Transaction: not observed",
        _ => $"Transaction: XACT_STATE {state}, from the last batch"
    };

    public static string DescribeStatus(QueryResult result) => result.Completion switch
    {
        QueryCompletionKind.Succeeded => "Succeeded",
        QueryCompletionKind.Failed => "Failed",
        QueryCompletionKind.CancellationRequested => "Cancellation requested",
        QueryCompletionKind.CancelledClientEnded => "Cancelled (client ended)",
        QueryCompletionKind.CancelledServerVerified => "Cancelled (server verified)",
        QueryCompletionKind.TimedOut => result.StopCause == StopCause.ProviderTimeout
            ? "Timed out (provider)"
            : "Timed out",
        QueryCompletionKind.UnknownAfterDisconnect => "Unknown after disconnect",
        QueryCompletionKind.FinalCompletionUnobserved => "Final completion unobserved",
        _ => result.Completion.ToString()
    };

    public static string FormatMessages(QueryResult result)
    {
        var msg = new StringBuilder();
        msg.AppendLine($"Status: {DescribeStatus(result)}");
        msg.AppendLine($"StopCause: {result.StopCause}");
        msg.AppendLine($"Target: {result.TargetConfidence}");
        if (result.TransactionState is int xact)
            msg.AppendLine($"XACT_STATE: {xact}");
        var affected = DescribeRowsAffected(result.RowsAffected);
        if (affected.Length > 0)
            msg.AppendLine(affected);
        var elapsed = DescribeClientElapsed(result.ClientElapsedMilliseconds);
        if (elapsed.Length > 0)
            msg.AppendLine(elapsed);
        if (!string.IsNullOrEmpty(result.ErrorSummary))
            msg.AppendLine(result.ErrorSummary);
        foreach (var m in result.Messages)
            msg.AppendLine(DescribeNotice(m));
        if (result.PartialResults)
            msg.AppendLine("Partial or truncated output.");
        if (result.RetentionLimitReached)
            msg.AppendLine("Retention limit reached.");
        return msg.ToString();
    }

    public static string FormatResults(QueryResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Status: {DescribeStatus(result)}");
        sb.AppendLine($"StopCause: {result.StopCause}");
        if (!string.IsNullOrEmpty(result.ErrorSummary))
            sb.AppendLine(result.ErrorSummary);
        sb.Append(ResultMaterializer.FormatResultSets(result));
        return sb.ToString();
    }

    /// <summary>
    /// Provider-reported row count. Empty when the provider did not report one, so a SELECT is not shown as zero rows affected.
    /// </summary>
    public static string DescribeRowsAffected(int? count)
    {
        if (count is not int n || n < 0)
            return "";
        var noun = n == 1 ? "row" : "rows";
        return n.ToString(CultureInfo.InvariantCulture) + " " + noun + " affected (provider-reported)";
    }

    /// <summary>
    /// Client-measured elapsed time for the batch. Empty when it was not measured.
    /// This is a stopwatch on this computer, not server statistics.
    /// </summary>
    public static string DescribeClientElapsed(long? milliseconds)
    {
        if (milliseconds is not long ms || ms < 0)
            return "";
        return "Client elapsed: " + ms.ToString(CultureInfo.InvariantCulture)
            + " ms (measured on this computer; not server statistics)";
    }

    /// <summary>
    /// SQL Server severity 0 and 10 are informational (PRINT is 0). Severities 1–9 are warnings.
    /// Severity 11 and above is an error. The label matches that, rather than calling every notice an error.
    /// </summary>
    public static string DescribeNotice(QueryMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return NoticeKind(message.Class)
            + " (severity " + message.Class.ToString(CultureInfo.InvariantCulture)
            + ", message " + message.Number.ToString(CultureInfo.InvariantCulture)
            + "): " + message.Text;
    }

    /// <summary>Short count for the results summary and the Messages tab. Empty when there are no notices.</summary>
    public static string DescribeNoticeSummary(IReadOnlyList<QueryMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var info = 0;
        var warning = 0;
        var error = 0;
        foreach (var message in messages)
        {
            switch (NoticeKind(message.Class))
            {
                case "Warning":
                    warning++;
                    break;
                case "Error":
                    error++;
                    break;
                default:
                    info++;
                    break;
            }
        }

        var parts = new List<string>();
        if (warning == 1)
            parts.Add("1 warning");
        else if (warning > 1)
            parts.Add(warning.ToString(CultureInfo.InvariantCulture) + " warnings");
        if (info == 1)
            parts.Add("1 info");
        else if (info > 1)
            parts.Add(info.ToString(CultureInfo.InvariantCulture) + " info");
        if (error == 1)
            parts.Add("1 error");
        else if (error > 1)
            parts.Add(error.ToString(CultureInfo.InvariantCulture) + " errors");
        return string.Join(", ", parts);
    }

    static string NoticeKind(byte severity) => severity switch
    {
        0 => "Info",
        < 10 => "Warning",
        10 => "Info",
        _ => "Error"
    };

    /// <summary>Status, results and messages must name the same completion outcome.</summary>
    public static bool UiSurfacesAgree(QueryResult result, out string detail)
    {
        var status = DescribeStatus(result);
        var results = FormatResults(result);
        var messages = FormatMessages(result);
        if (!results.Contains(status, StringComparison.Ordinal))
        {
            detail = $"results missing status '{status}'";
            return false;
        }
        if (!messages.Contains(status, StringComparison.Ordinal))
        {
            detail = $"messages missing status '{status}'";
            return false;
        }
        detail = status;
        return true;
    }
}
