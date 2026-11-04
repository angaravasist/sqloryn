using System.Globalization;
using System.Text;

namespace Sqloryn.Core;

/// <summary>Runs one bounded, parameterised, read-only msdb query. Implementations must not retry or batch other statements.</summary>
public interface IAgentQueryRunner
{
    Task<RawRows> QueryAsync(string sql, IReadOnlyList<EditParameter> parameters, int maxRows, CancellationToken cancellationToken);
}

/// <summary>Runs exactly one <see cref="AgentCommand"/> (its fixed SQL and bound parameters) and nothing else.</summary>
public interface IAgentCommandRunner
{
    Task ExecuteAsync(AgentCommand command, CancellationToken cancellationToken);
}

public enum AgentAvailability
{
    Available,
    /// <summary>Not probed, or the probe was denied/failed. Reads may be attempted; changes are blocked.</summary>
    Unknown,
    /// <summary>The target has no SQL Server Agent (for example Azure SQL Database).</summary>
    Unavailable
}

public sealed record AgentGate(AgentAvailability Availability, string Message)
{
    public bool CanRead => Availability != AgentAvailability.Unavailable;
    /// <summary>Run/Delete are only allowed when Agent was positively observed.</summary>
    public bool CanChange => Availability == AgentAvailability.Available;
}

/// <summary>Agent run status. Values 0–4 match <c>sysjobhistory.run_status</c>; 5 is "unknown / never run".</summary>
public enum AgentRunStatus { Failed = 0, Succeeded = 1, Retry = 2, Canceled = 3, InProgress = 4, Unknown = 5 }

public static class AgentStatuses
{
    public static AgentRunStatus FromRunStatus(int value) => value is >= 0 and <= 4 ? (AgentRunStatus)value : AgentRunStatus.Unknown;
}

/// <summary>Agent stores dates and times as yyyymmdd / hhmmss integers in the SERVER's local time, with no zone or offset.</summary>
public static class AgentTime
{
    /// <summary>Returns a <see cref="DateTimeKind.Unspecified"/> value (server local time), or null for 0 or an invalid combination.</summary>
    public static DateTime? ParseDateTime(int date, int time)
    {
        if (date <= 0 || time < 0)
            return null;
        int y = date / 10000, mo = date / 100 % 100, d = date % 100;
        int h = time / 10000, mi = time / 100 % 100, s = time % 100;
        if (y is < 1753 or > 9999 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || s > 59)
            return null;
        return new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified);
    }

    /// <summary><c>run_duration</c> is hhmmss as an integer; hours are not capped at 24.</summary>
    public static TimeSpan? ParseDuration(int value)
    {
        if (value < 0)
            return null;
        int h = value / 10000, m = value / 100 % 100, s = value % 100;
        return m > 59 || s > 59 ? null : new TimeSpan(h, m, s);
    }

    /// <summary>The value is a wall-clock reading with no zone; it is always labelled so it is not mistaken for local or UTC time.</summary>
    public static string Format(DateTime? value)
        => value is { } v ? v.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " (server time)" : "—";

    public static string FormatDuration(TimeSpan? value)
        => value is { } v ? $"{(int)v.TotalHours}:{v.Minutes:00}:{v.Seconds:00}" : "—";
}

/// <summary>Plain-language schedule text from <c>sysschedules</c> columns. It describes the definition, not the next-run time.</summary>
public static class AgentSchedules
{
    static readonly string[] Days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public static string Describe(int freqType, int freqInterval, int subdayType, int subdayInterval, int activeStartTime, int recurrence = 1)
    {
        var sb = new StringBuilder();
        switch (freqType)
        {
            case 1:
                sb.Append("Once");
                break;
            case 4:
                sb.Append(freqInterval <= 1 ? "Every day" : $"Every {freqInterval} days");
                break;
            case 8:
            {
                sb.Append(recurrence <= 1 ? "Every week" : $"Every {recurrence} weeks");
                var days = Enumerable.Range(0, 7).Where(i => (freqInterval & (1 << i)) != 0).Select(i => Days[i]).ToList();
                if (days.Count > 0)
                    sb.Append(" on ").Append(string.Join(", ", days));
                break;
            }
            case 16:
                sb.Append(recurrence <= 1 ? "Every month" : $"Every {recurrence} months").Append(" on day ").Append(freqInterval);
                break;
            case 32:
                sb.Append(recurrence <= 1 ? "Every month" : $"Every {recurrence} months").Append(" (relative day of month)");
                break;
            case 64:
                return "When SQL Server Agent starts";
            case 128:
                return "When the computer is idle";
            default:
                return "Unrecognised schedule type";
        }

        var unit = subdayType switch { 2 => "seconds", 4 => "minutes", 8 => "hours", _ => null };
        if (unit is not null && subdayInterval > 0)
            sb.Append(", every ").Append(subdayInterval).Append(' ').Append(unit);
        else if (freqType != 1 || activeStartTime > 0)
            sb.Append(" at ").Append(HhMmSs(activeStartTime));
        return sb.ToString();
    }

    static string HhMmSs(int time)
        => time is >= 0 and <= 235959 && time / 100 % 100 < 60 && time % 100 < 60
            ? $"{time / 10000:00}:{time / 100 % 100:00}:{time % 100:00}"
            : "an unrecognised time";
}

public sealed record AgentJob(Guid JobId, string Name, bool Enabled, string? Description, AgentRunStatus LastStatus, DateTime? LastRun, DateTime? NextRun);

public sealed record AgentSchedule(string Name, bool Enabled, string Description, DateTime? NextRun);

/// <param name="StepId">0 is the job outcome row; 1+ are step rows.</param>
public sealed record AgentHistoryEntry(long InstanceId, int StepId, string StepName, AgentRunStatus Status, DateTime? RunTime, TimeSpan? Duration, string Message);

/// <summary>A bounded read. <see cref="Failure"/> non-null means the read did not happen; an empty list with no failure means there really are no rows.</summary>
public sealed record AgentListing<T>(IReadOnlyList<T> Items, bool Truncated, CatalogFailure? Failure);

/// <summary>
/// Read-only SQL Server Agent browsing from msdb. Every query is a bounded SELECT; job ids are bound as parameters.
/// Denied roles, disabled Agent and failures are reported as <see cref="CatalogFailure"/>, never as an empty list.
/// </summary>
public static class AgentCatalog
{
    public const int MaxJobs = 500;
    public const int MaxSchedules = 100;
    public const int MaxHistory = 200;
    public const int MaxMessageChars = 500;
    const int MaxNameChars = 200;

    internal static readonly string JobsSql = $"""
        SELECT TOP ({MaxJobs + 1}) j.job_id, j.name, CAST(j.enabled AS int), j.description,
               CAST(ISNULL(s.last_run_outcome, 5) AS int), CAST(ISNULL(s.last_run_date, 0) AS int), CAST(ISNULL(s.last_run_time, 0) AS int),
               CAST(ISNULL(n.next_run_date, 0) AS int), CAST(ISNULL(n.next_run_time, 0) AS int)
        FROM msdb.dbo.sysjobs AS j
        LEFT JOIN msdb.dbo.sysjobservers AS s ON s.job_id = j.job_id AND s.server_id = 0
        OUTER APPLY (SELECT TOP (1) js.next_run_date, js.next_run_time FROM msdb.dbo.sysjobschedules AS js
                     WHERE js.job_id = j.job_id AND js.next_run_date > 0 ORDER BY js.next_run_date, js.next_run_time) AS n
        ORDER BY j.name, j.job_id;
        """;

    internal static readonly string SchedulesSql = $"""
        SELECT TOP ({MaxSchedules + 1}) sc.name, CAST(sc.enabled AS int), CAST(sc.freq_type AS int), CAST(sc.freq_interval AS int),
               CAST(sc.freq_subday_type AS int), CAST(sc.freq_subday_interval AS int), CAST(sc.freq_recurrence_factor AS int),
               CAST(sc.active_start_time AS int), CAST(ISNULL(js.next_run_date, 0) AS int), CAST(ISNULL(js.next_run_time, 0) AS int)
        FROM msdb.dbo.sysjobschedules AS js
        JOIN msdb.dbo.sysschedules AS sc ON sc.schedule_id = js.schedule_id
        WHERE js.job_id = @job_id
        ORDER BY sc.name, sc.schedule_id;
        """;

    internal static readonly string HistorySql = $"""
        SELECT TOP ({MaxHistory + 1}) h.instance_id, CAST(h.step_id AS int), h.step_name, CAST(h.run_status AS int),
               CAST(h.run_date AS int), CAST(h.run_time AS int), CAST(h.run_duration AS int), h.message
        FROM msdb.dbo.sysjobhistory AS h
        WHERE h.job_id = @job_id
        ORDER BY h.instance_id DESC;
        """;

    /// <summary>Maps the probed <see cref="ConnectionCapabilities"/> to what the Agent window may do.</summary>
    public static AgentGate Gate(ConnectionCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.SupportsAgent switch
        {
            true => new AgentGate(AgentAvailability.Available, "SQL Server Agent is running on this server."),
            false => new AgentGate(AgentAvailability.Unavailable,
                "SQL Server Agent is not available on this target" +
                (capabilities.Notes.FirstOrDefault(n => n.Contains("Agent", StringComparison.Ordinal)) is { } note ? ": " + note : " (the service is not running or the engine edition does not include it).")),
            _ => new AgentGate(AgentAvailability.Unknown,
                "SQL Server Agent availability is unknown (not probed, or the probe was denied). Jobs can be listed read-only; Run and Delete are disabled.")
        };
    }

    public static async Task<AgentListing<AgentJob>> ListJobsAsync(IAgentQueryRunner runner, CancellationToken cancellationToken = default)
        => await ReadAsync(runner, JobsSql, [], MaxJobs, "SQL Server Agent jobs", ParseJob, cancellationToken).ConfigureAwait(false);

    public static async Task<AgentListing<AgentSchedule>> ListSchedulesAsync(IAgentQueryRunner runner, Guid jobId, CancellationToken cancellationToken = default)
        => await ReadAsync(runner, SchedulesSql, [new EditParameter("@job_id", jobId)], MaxSchedules, "job schedules", ParseSchedule, cancellationToken).ConfigureAwait(false);

    public static async Task<AgentListing<AgentHistoryEntry>> ListHistoryAsync(IAgentQueryRunner runner, Guid jobId, CancellationToken cancellationToken = default)
        => await ReadAsync(runner, HistorySql, [new EditParameter("@job_id", jobId)], MaxHistory, "job history", ParseHistory, cancellationToken).ConfigureAwait(false);

    static async Task<AgentListing<T>> ReadAsync<T>(
        IAgentQueryRunner runner, string sql, IReadOnlyList<EditParameter> parameters, int max, string what,
        Func<object?[], T?> parse, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(runner);
        try
        {
            var rows = await runner.QueryAsync(sql, parameters, max, cancellationToken).ConfigureAwait(false);
            var items = new List<T>(rows.Rows.Count);
            foreach (var row in rows.Rows)
            {
                if (items.Count >= max)
                    return new AgentListing<T>(items, true, null);
                if (parse(row) is { } item)
                    items.Add(item);
            }
            return new AgentListing<T>(items, rows.Truncated, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new AgentListing<T>([], false, MapFailure(ex, what));
        }
    }

    /// <summary>A denied read names the msdb Agent roles that grant access; other failures keep the redacted single-line server text.</summary>
    public static CatalogFailure MapFailure(Exception ex, string what)
    {
        var failure = CatalogFailure.From(ex, what);
        return failure.Kind == CatalogFailureKind.Denied
            ? failure with
            {
                Text = $"Denied: this login cannot read {what} from msdb (SQL error {failure.Number}). " +
                       "Agent access needs SQLAgentUserRole, SQLAgentReaderRole or SQLAgentOperatorRole in msdb (or sysadmin). " +
                       "An empty list would not mean there are no jobs."
            }
            : failure;
    }

    // ---- row parsing: positional, defensive; a malformed row is skipped, never guessed -----------------------------

    static AgentJob? ParseJob(object?[] r)
    {
        if (r.Length < 9 || !TryGuid(r[0], out var id))
            return null;
        return new AgentJob(
            id,
            Text(r[1], MaxNameChars),
            Int(r[2]) != 0,
            r[3] is null or DBNull ? null : Text(r[3], MaxMessageChars),
            AgentStatuses.FromRunStatus(Int(r[4])),
            AgentTime.ParseDateTime(Int(r[5]), Int(r[6])),
            AgentTime.ParseDateTime(Int(r[7]), Int(r[8])));
    }

    static AgentSchedule? ParseSchedule(object?[] r)
    {
        if (r.Length < 10)
            return null;
        return new AgentSchedule(
            Text(r[0], MaxNameChars),
            Int(r[1]) != 0,
            AgentSchedules.Describe(Int(r[2]), Int(r[3]), Int(r[4]), Int(r[5]), Int(r[7]), Int(r[6])),
            AgentTime.ParseDateTime(Int(r[8]), Int(r[9])));
    }

    static AgentHistoryEntry? ParseHistory(object?[] r)
    {
        if (r.Length < 8)
            return null;
        return new AgentHistoryEntry(
            Convert.ToInt64(r[0], CultureInfo.InvariantCulture),
            Int(r[1]),
            Text(r[2], MaxNameChars),
            AgentStatuses.FromRunStatus(Int(r[3])),
            AgentTime.ParseDateTime(Int(r[4]), Int(r[5])),
            AgentTime.ParseDuration(Int(r[6])),
            Text(r[7], MaxMessageChars));
    }

    static bool TryGuid(object? value, out Guid id)
    {
        if (value is Guid g)
        {
            id = g;
            return true;
        }
        return Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out id);
    }

    static int Int(object? value)
        => value is null or DBNull or RawCellOmitted ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    /// <summary>Server text is untrusted: redacted, single-line and bounded. An oversized cell the reader refused to load is named.</summary>
    internal static string Text(object? value, int max)
    {
        var raw = value switch
        {
            null or DBNull => "",
            RawCellOmitted omitted => $"({omitted.Reason})",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
        var line = SchemaObjectCatalog.OneLine(DiagnosticsRedactor.Redact(raw)).Trim();
        return line.Length <= max ? line : line[..max] + "…";
    }
}

public enum AgentCommandKind { StartJob, DeleteJob }

/// <summary>
/// One Agent change. The SQL text is one of two fixed constants; the job id is the only input and is bound as a parameter,
/// so the job name (untrusted) never reaches SQL.
/// </summary>
public sealed record AgentCommand
{
    public AgentCommandKind Kind { get; }
    public Guid JobId { get; }
    public string JobName { get; }
    public string Sql { get; }
    public IReadOnlyList<EditParameter> Parameters { get; }
    /// <summary>Single-line description shown in the confirmation dialog before anything is sent.</summary>
    public string Preview { get; }

    AgentCommand(AgentCommandKind kind, Guid jobId, string jobName, string sql, string preview)
    {
        Kind = kind;
        JobId = jobId;
        JobName = AgentCatalog.Text(jobName, 200);
        Sql = sql;
        Parameters = [new EditParameter("@job_id", jobId)];
        Preview = preview.Replace("{name}", JobName, StringComparison.Ordinal).Replace("{id}", jobId.ToString("D"), StringComparison.Ordinal);
    }

    public static AgentCommand StartJob(Guid jobId, string jobName) => new(
        AgentCommandKind.StartJob, jobId, jobName,
        "EXEC msdb.dbo.sp_start_job @job_id = @job_id;",
        "Start Agent job \"{name}\" ({id}) now. The job runs its steps on the server with their own credentials and side effects; " +
        "Cancel here does not stop it once started.");

    public static AgentCommand DeleteJob(Guid jobId, string jobName) => new(
        AgentCommandKind.DeleteJob, jobId, jobName,
        "EXEC msdb.dbo.sp_delete_job @job_id = @job_id;",
        "Permanently delete Agent job \"{name}\" ({id}), its steps, its run history and any schedules used by no other job. This cannot be undone.");
}

public enum AgentActionOutcome
{
    Done,
    /// <summary>Refused before anything was sent (not confirmed, or Agent not positively available).</summary>
    Rejected,
    Denied,
    /// <summary>The server returned an error; the action did not take effect.</summary>
    Failed,
    /// <summary>Timeout, cancel or lost connection: the server may or may not have acted. Read job history to find out.</summary>
    OutcomeUnknown
}

public sealed record AgentActionResult(AgentActionOutcome Outcome, string Message);

public static class AgentActions
{
    /// <summary>
    /// Runs <paramref name="command"/> only when <paramref name="confirmed"/> is true (the caller shows the preview and gets an
    /// explicit yes) and the gate says Agent was observed available. A client timeout is reported as unknown, never as failure.
    /// </summary>
    public static async Task<AgentActionResult> ExecuteAsync(
        IAgentCommandRunner runner, AgentGate gate, AgentCommand command, bool confirmed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(command);
        if (!confirmed)
            return new AgentActionResult(AgentActionOutcome.Rejected, "Not confirmed. Nothing was sent to the server.");
        if (!gate.CanChange)
            return new AgentActionResult(AgentActionOutcome.Rejected, gate.Message + " Nothing was sent to the server.");

        var verb = command.Kind == AgentCommandKind.StartJob ? "start" : "delete";
        try
        {
            await runner.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var failure = CatalogFailure.From(ex, $"the {verb} request");
            if (failure.Kind == CatalogFailureKind.Cancelled || failure.Number is -1 or -2)
                return new AgentActionResult(AgentActionOutcome.OutcomeUnknown,
                    $"The client stopped waiting before the server answered, so the {verb} request may or may not have taken effect. " +
                    "Refresh the job list and read its history to see what the server did.");
            if (failure.Kind == CatalogFailureKind.Denied)
                return new AgentActionResult(AgentActionOutcome.Denied,
                    $"Denied: this login cannot {verb} Agent jobs (SQL error {failure.Number}). " +
                    "It needs SQLAgentOperatorRole (or ownership of the job, or sysadmin) in msdb. Nothing changed.");
            var detail = AgentCatalog.Text(ex.Message, AgentCatalog.MaxMessageChars);
            return new AgentActionResult(AgentActionOutcome.Failed,
                $"The server did not {verb} the job{(failure.Number == 0 ? "" : $" (SQL error {failure.Number})")}: {detail}");
        }

        return command.Kind == AgentCommandKind.StartJob
            ? new AgentActionResult(AgentActionOutcome.Done,
                "The server accepted the start request. That does not mean the job succeeded: read its history for the outcome.")
            : new AgentActionResult(AgentActionOutcome.Done, "The server deleted the job and its history. Refresh to confirm.");
    }
}
