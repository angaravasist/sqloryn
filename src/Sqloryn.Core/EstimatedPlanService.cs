using System.Text;

namespace Sqloryn.Core;

/// <summary>
/// The only two operations an estimated-plan run may perform on a connection. The control statement text lives in the
/// implementation (<c>SET SHOWPLAN_XML ON|OFF</c>); user SQL can only travel through <see cref="RunBatchAsync"/>,
/// which implementations must refuse unless SHOWPLAN_XML was confirmed ON by <see cref="SetShowplanXmlAsync"/>.
/// </summary>
public interface IPlanCommandRunner
{
    /// <summary>Runs <c>SET SHOWPLAN_XML ON</c> or <c>OFF</c> as its own batch. Throws <see cref="PlanCommandException"/> on server errors.</summary>
    Task SetShowplanXmlAsync(bool on, CancellationToken cancellationToken);

    /// <summary>
    /// Submits one user batch while SHOWPLAN_XML is ON, so the server compiles it and returns plan XML (one entry per
    /// statement) without executing it. Throws <see cref="PlanCommandException"/> on server errors.
    /// </summary>
    Task<IReadOnlyList<string>> RunBatchAsync(string batch, CancellationToken cancellationToken);
}

/// <summary>A provider-neutral server failure raised by an <see cref="IPlanCommandRunner"/>.</summary>
public sealed class PlanCommandException(int? number, string message, Exception? inner = null) : Exception(message, inner)
{
    public int? Number { get; } = number;
}

/// <summary>
/// Reads one plan document in fixed-size chunks and stops as soon as it exceeds the budget, so a hostile or huge plan is
/// never materialised in full. Over-budget input is an error, never a shortened document.
/// </summary>
internal static class PlanXmlReader
{
    internal const int ChunkCharacters = 8 * 1024;

    /// <param name="getChars">Sequential <c>(fieldOffset, buffer, length) =&gt; charsRead</c>; returns 0 at the end of the value.</param>
    /// <param name="maxCharacters">Hard cap on the returned document.</param>
    internal static string ReadBounded(Func<long, char[], int, int> getChars, long maxCharacters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(getChars);
        var sb = new StringBuilder();
        var buffer = new char[ChunkCharacters];
        long offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = getChars(offset, buffer, buffer.Length);
            if (read <= 0)
                return sb.ToString();
            offset += read;
            if (offset > maxCharacters)
                throw new PlanCommandException(null,
                    $"The estimated plan is too large to display (more than {maxCharacters:N0} characters).");
            sb.Append(buffer, 0, read);
        }
    }
}

public enum EstimatedPlanStatus
{
    Succeeded,
    /// <summary>Refused before anything was sent to the server.</summary>
    Rejected,
    PermissionDenied,
    Failed,
    Cancelled
}

/// <param name="PlanXml">One plan document per estimated statement, in order. May be partial when the status is not Succeeded.</param>
/// <param name="SessionTainted">SHOWPLAN could not be switched off again; the caller must drop the connection.</param>
public sealed record EstimatedPlanResult(
    EstimatedPlanStatus Status,
    IReadOnlyList<string> PlanXml,
    string? Error,
    bool SessionTainted);

/// <summary>
/// Estimated execution plans via <c>SET SHOWPLAN_XML</c>. The batch is delivered only after SHOWPLAN_XML ON succeeded,
/// so the server compiles but never executes it. SHOWPLAN_XML OFF is always attempted afterwards.
/// </summary>
public static class EstimatedPlanService
{
    /// <summary>Per-plan size limit. A larger plan is reported as an error, never shortened.</summary>
    public const int MaxPlanCharacters = 4 * 1024 * 1024;

    /// <summary>
    /// Limit on the summed characters of every plan across every batch of one estimate. Exceeding it fails the estimate
    /// before any further batch is sent; plans are never shortened.
    /// </summary>
    public const int MaxTotalPlanCharacters = 8 * 1024 * 1024;

    /// <summary>Server errors meaning the caller lacks SHOWPLAN (262), VIEW SERVER STATE (300) or object (229/297) permission.</summary>
    static readonly HashSet<int> PermissionErrors = [229, 262, 297, 300];

    static readonly string[] ForbiddenSetOptions = ["STATISTICS", "NOEXEC", "PARSEONLY", "FMTONLY"];

    public static async Task<EstimatedPlanResult> EstimateAsync(
        IPlanCommandRunner runner, string sql, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        if (string.IsNullOrWhiteSpace(sql))
            return Rejected("There is no SQL to estimate. Nothing was executed.");

        IReadOnlyList<string> batches;
        try
        {
            var script = GoBatchSplitter.Parse(sql);
            batches = script.Batches.Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        }
        catch (SqlScriptRejectedException ex)
        {
            return Rejected(ex.Message);
        }
        if (batches.Count == 0)
            return Rejected("There is no SQL to estimate. Nothing was executed.");

        foreach (var batch in batches)
        {
            if (FindForbiddenSet(batch) is { } option)
                return Rejected(
                    $"SET {option} cannot appear in a batch being estimated because it could defeat SHOWPLAN. Nothing was executed.");
        }

        var plans = new List<string>();
        var showplanRequested = false;
        EstimatedPlanStatus status = EstimatedPlanStatus.Succeeded;
        string? error = null;
        var tainted = false;
        long totalCharacters = 0;
        try
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                showplanRequested = true;
                await runner.SetShowplanXmlAsync(true, cancellationToken).ConfigureAwait(false);

                // Only reached when SET SHOWPLAN_XML ON returned normally.
                foreach (var batch in batches)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await runner.RunBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                    foreach (var plan in result)
                    {
                        if (plan.Length > MaxPlanCharacters)
                            throw new PlanCommandException(null,
                                $"The estimated plan is too large to display ({plan.Length:N0} characters; limit {MaxPlanCharacters:N0}).");
                        // An over-budget plan is not retained (and never shortened); the loop ends and OFF is attempted.
                        if (totalCharacters + plan.Length > MaxTotalPlanCharacters)
                            throw new PlanCommandException(null,
                                $"The estimated plans are too large to display in total (more than {MaxTotalPlanCharacters:N0} characters across all plans).");
                        totalCharacters += plan.Length;
                        plans.Add(plan);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                status = EstimatedPlanStatus.Cancelled;
                error = "Estimating the plan was cancelled. The statement was not executed.";
            }
            catch (PlanCommandException ex)
            {
                if (ex.Number is { } n && PermissionErrors.Contains(n))
                {
                    status = EstimatedPlanStatus.PermissionDenied;
                    error = "The SHOWPLAN permission is required to display an estimated plan and the server refused it. " +
                            "The statement was not executed. Server message: " + OneLine(ex.Message);
                }
                else
                {
                    status = EstimatedPlanStatus.Failed;
                    error = "The estimated plan could not be produced: " + OneLine(ex.Message) + " The statement was not executed.";
                }
            }
            catch (Exception ex)
            {
                // Non-PlanCommand failures (e.g. InvalidOperationException from the runner) must still turn SHOWPLAN off
                // and force the session to drop — we cannot prove the connection mode afterwards.
                status = EstimatedPlanStatus.Failed;
                error = "The estimated plan could not be produced: " + OneLine(ex.Message) + " The statement was not executed.";
                tainted = true;
            }
        }
        finally
        {
            if (showplanRequested)
            {
                using var offCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await runner.SetShowplanXmlAsync(false, offCts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Any failure to confirm the reset (not only provider errors) leaves the connection mode unprovable.
                    tainted = true;
                    var extra = "SET SHOWPLAN_XML OFF failed, so this connection may still be in SHOWPLAN mode and will be closed. " +
                                "Statements were not executed.";
                    error = error is null ? extra : error + " " + extra;
                    if (status == EstimatedPlanStatus.Succeeded)
                        status = EstimatedPlanStatus.Failed;
                }
            }
        }

        return new EstimatedPlanResult(status, plans, error, tainted);
    }

    static EstimatedPlanResult Rejected(string message)
        => new(EstimatedPlanStatus.Rejected, [], message, false);

    /// <summary>Plain text for the Messages pane. Always states that nothing was executed.</summary>
    public static string FormatForMessages(EstimatedPlanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var sb = new StringBuilder();
        if (result.Status == EstimatedPlanStatus.Succeeded)
        {
            sb.Append("Estimated execution plan (SHOWPLAN_XML). The statement was not executed.\n");
            for (var i = 0; i < result.PlanXml.Count; i++)
            {
                sb.Append("\n-- Plan ").Append(i + 1).Append(" of ").Append(result.PlanXml.Count).Append('\n');
                sb.Append(result.PlanXml[i]).Append('\n');
            }
            if (result.PlanXml.Count == 0)
                sb.Append("\nThe server returned no plan for this SQL.\n");
            return sb.ToString();
        }

        sb.Append("Estimated plan unavailable (").Append(result.Status).Append("). The statement was not executed.\n");
        if (result.Error is not null)
            sb.Append('\n').Append(result.Error).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Finds <c>SET STATISTICS|NOEXEC|PARSEONLY|FMTONLY|SHOWPLAN_*</c> outside comments, strings and quoted identifiers.
    /// Such statements could change execution mode mid-batch, so a batch containing them is not estimated.
    /// </summary>
    internal static string? FindForbiddenSet(string batch)
    {
        string? previous = null;
        foreach (var token in Tokens(batch))
        {
            if (previous is not null && previous.Equals("SET", StringComparison.OrdinalIgnoreCase) &&
                (token.StartsWith("SHOWPLAN_", StringComparison.OrdinalIgnoreCase) ||
                 ForbiddenSetOptions.Contains(token, StringComparer.OrdinalIgnoreCase)))
                return token.ToUpperInvariant();
            previous = token;
        }
        return null;
    }

    static IEnumerable<string> Tokens(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var depth = 1;
                i += 2;
                while (i < text.Length && depth > 0)
                {
                    if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*') { depth++; i += 2; }
                    else if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
            }
            else if (c is '\'' or '"' or '[')
            {
                var close = c == '[' ? ']' : c;
                i++;
                while (i < text.Length)
                {
                    if (text[i] == close)
                    {
                        if (i + 1 < text.Length && text[i + 1] == close) { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                // A quoted thing between SET and an option name breaks adjacency; model it as an opaque token.
                yield return "\0";
            }
            else if (char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '@' or '#' or '$'))
                    i++;
                yield return text[start..i];
            }
            else
            {
                i++;
            }
        }
    }

    static string OneLine(string message)
    {
        var flat = message.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= 400 ? flat : flat[..400] + "…";
    }
}
