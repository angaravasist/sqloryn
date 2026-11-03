using System.Text;

namespace Sqloryn.Core;

/// <summary>
/// Single user-visible status mapping shared by the tab status bar, results pane and messages.
/// </summary>
public static class QueryOutcomeFormatter
{
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
        if (!string.IsNullOrEmpty(result.ErrorSummary))
            msg.AppendLine(result.ErrorSummary);
        foreach (var m in result.Messages)
            msg.AppendLine($"Msg {m.Number} (class {m.Class}): {m.Text}");
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
