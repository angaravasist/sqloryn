using System.Text.RegularExpressions;
using Sqloryn.Core;

/// <summary>
/// Agent and plans: SafeShowPlanXml (XXE/DTD rejected, operator extraction), estimated-plan operator summary,
/// AgentCatalog (bounded msdb reads, denial mapping, capability gate), confirmed Agent actions and App wiring scans.
/// Component-only: fakes, no SQL Server connection, so nothing here is live Agent evidence.
/// </summary>
static class AgentTests
{
    const string Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"FAIL {name}");
    }

    static void Pass(string name) => Console.WriteLine($"PASS {name}");

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SQLoryn.slnx")))
                return d.FullName;
        throw new Exception("FAIL repo root (SQLoryn.slnx) not found from test base directory");
    }

    public static async Task RunAsync()
    {
        SafePlanParsing();
        PlanFormatting();
        AgentTimeAndText();
        await CatalogReads();
        await Denial();
        Gate();
        await Actions();
        AppWiring();
    }

    // ---- plans -------------------------------------------------------------------------------------------------

    static string BenignPlan() => $"""
        <?xml version="1.0" encoding="utf-16"?>
        <ShowPlanXML xmlns="{Ns}" Version="1.6" Build="16.0.1000.6">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT * FROM t" StatementType="SELECT">
              <QueryPlan>
                <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimatedTotalSubtreeCost="0.0182">
                  <NestedLoops>
                    <RelOp NodeId="1" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimatedTotalSubtreeCost="0.0033"><IndexScan/></RelOp>
                    <RelOp NodeId="2" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimatedTotalSubtreeCost="0.01"><IndexScan/></RelOp>
                  </NestedLoops>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    static void SafePlanParsing()
    {
        var ok = SafeShowPlanXml.Parse(BenignPlan());
        Check(ok.Parsed && ok.Error is null && ok.Operators.Count == 3, "ws9-plan-benign-parsed");
        Check(ok.Operators[0].PhysicalOp == "Nested Loops" && ok.Operators[0].Depth == 0 && ok.Operators[0].NodeId == 0, "ws9-plan-root-operator");
        Check(ok.Operators[1].Depth == 1 && ok.Operators[2].Depth == 1 && ok.Operators[2].LogicalOp == "Clustered Index Scan", "ws9-plan-child-depth");
        Check(ok.Operators[0].EstimatedSubtreeCost is { } c && Math.Abs(c - 0.0182) < 1e-9, "ws9-plan-subtree-cost");
        Pass("ws9-plan-benign");

        // Missing cost is null, not zero; garbage cost is null; hostile names are single-line and bounded.
        var odd = SafeShowPlanXml.Parse(
            $"<ShowPlanXML xmlns=\"{Ns}\"><RelOp NodeId=\"x\" PhysicalOp=\"Scan&#10;line2\" LogicalOp=\"{new string('L', 500)}\" EstimatedTotalSubtreeCost=\"NaN\"/>" +
            $"<RelOp PhysicalOp=\"Sort\" LogicalOp=\"Sort\"/></ShowPlanXML>");
        Check(odd.Parsed && odd.Operators.Count == 2 && odd.Operators[0].EstimatedSubtreeCost is null && odd.Operators[1].EstimatedSubtreeCost is null, "ws9-plan-missing-cost-null");
        Check(!odd.Operators[0].PhysicalOp.Contains('\n') && odd.Operators[0].NodeId is null && odd.Operators[0].LogicalOp.Length <= 81, "ws9-plan-operator-text-sanitised");

        // Wrong namespace is not a showplan operator.
        var foreign = SafeShowPlanXml.Parse("<ShowPlanXML><RelOp PhysicalOp=\"Sort\" LogicalOp=\"Sort\"/></ShowPlanXML>");
        Check(foreign.Parsed && foreign.Operators.Count == 0, "ws9-plan-foreign-namespace-ignored");

        // XXE: external entity must be rejected, never resolved or echoed.
        var secretFile = Path.Combine(Path.GetTempPath(), "sqloryn-xxe-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(secretFile, "XXE-SECRET-CONTENT");
        try
        {
            var xxe = $"""
                <?xml version="1.0"?>
                <!DOCTYPE ShowPlanXML [<!ENTITY xxe SYSTEM "{new Uri(secretFile).AbsoluteUri}">]>
                <ShowPlanXML xmlns="{Ns}"><RelOp NodeId="0" PhysicalOp="&xxe;" LogicalOp="x"/></ShowPlanXML>
                """;
            var r = SafeShowPlanXml.Parse(xxe);
            Check(!r.Parsed && r.Operators.Count == 0 && r.Error is not null, "ws9-xxe-rejected");
            Check(!r.Error!.Contains("XXE-SECRET", StringComparison.Ordinal) && !r.Error.Contains(secretFile, StringComparison.Ordinal), "ws9-xxe-no-leak");
        }
        finally { File.Delete(secretFile); }

        // Any DOCTYPE (even internal-only) and entity-expansion bombs are rejected.
        var internalDtd = SafeShowPlanXml.Parse($"<!DOCTYPE a [<!ENTITY e \"v\">]><ShowPlanXML xmlns=\"{Ns}\"><RelOp PhysicalOp=\"&e;\" LogicalOp=\"x\"/></ShowPlanXML>");
        Check(!internalDtd.Parsed, "ws9-dtd-internal-rejected");
        var bomb = "<!DOCTYPE l [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]>" +
                   $"<ShowPlanXML xmlns=\"{Ns}\"><RelOp PhysicalOp=\"&c;\" LogicalOp=\"x\"/></ShowPlanXML>";
        Check(!SafeShowPlanXml.Parse(bomb).Parsed, "ws9-entity-bomb-rejected");
        Check(!SafeShowPlanXml.Parse("<ShowPlanXML><unclosed>").Parsed && !SafeShowPlanXml.Parse("").Parsed && !SafeShowPlanXml.Parse(null!).Parsed, "ws9-malformed-rejected");

        // Operator list is bounded and says so.
        var many = $"<ShowPlanXML xmlns=\"{Ns}\">" +
                   string.Concat(Enumerable.Range(0, SafeShowPlanXml.MaxOperators + 25).Select(i => $"<RelOp NodeId=\"{i}\" PhysicalOp=\"Sort\" LogicalOp=\"Sort\"/>")) +
                   "</ShowPlanXML>";
        var bounded = SafeShowPlanXml.Parse(many);
        Check(bounded.Parsed && bounded.Truncated && bounded.Operators.Count == SafeShowPlanXml.MaxOperators, "ws9-plan-operators-bounded");

        var core = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Sqloryn.Core", "SafeShowPlanXml.cs"));
        Check(core.Contains("DtdProcessing.Prohibit", StringComparison.Ordinal) && core.Contains("XmlResolver = null", StringComparison.Ordinal)
              && !core.Contains("DtdProcessing.Parse", StringComparison.Ordinal) && !core.Contains("XmlUrlResolver", StringComparison.Ordinal)
              && !core.Contains("ExecuteAsync", StringComparison.Ordinal), "ws9-safe-plan-source-hardened");
        Pass("ws9-plan-xxe-safe");
    }

    static void PlanFormatting()
    {
        var ok = new EstimatedPlanResult(EstimatedPlanStatus.Succeeded, [BenignPlan()], null, false);
        var text = EstimatedPlanService.FormatForMessages(ok);
        Check(text.Contains("Operators (3)", StringComparison.Ordinal) && text.Contains("Nested Loops", StringComparison.Ordinal)
              && text.Contains("subtree cost 0.0182", StringComparison.Ordinal), "ws9-plan-summary-lines");
        Check(text.Contains("<ShowPlanXML", StringComparison.Ordinal) && text.Contains("EstimatedTotalSubtreeCost=\"0.0033\"", StringComparison.Ordinal), "ws9-plan-full-xml-kept");
        Check(text.IndexOf("Operators (3)", StringComparison.Ordinal) < text.IndexOf("<ShowPlanXML", StringComparison.Ordinal), "ws9-plan-summary-before-xml");
        Check(text.Contains("not executed", StringComparison.OrdinalIgnoreCase), "ws9-plan-format-says-not-executed");

        var rejected = new EstimatedPlanResult(EstimatedPlanStatus.Succeeded,
            [$"<!DOCTYPE a [<!ENTITY e SYSTEM \"file:///etc/hosts\">]><ShowPlanXML xmlns=\"{Ns}\"/>"], null, false);
        var rejectedText = EstimatedPlanService.FormatForMessages(rejected);
        Check(rejectedText.Contains("Operator summary unavailable", StringComparison.Ordinal) && rejectedText.Contains("<!DOCTYPE", StringComparison.Ordinal), "ws9-plan-unparseable-still-shows-xml");
        Pass("ws9-plan-format");
    }

    // ---- agent time / text -------------------------------------------------------------------------------------

    static void AgentTimeAndText()
    {
        Check(AgentTime.ParseDateTime(20261104, 93005) == new DateTime(2026, 11, 4, 9, 30, 5, DateTimeKind.Unspecified), "ws9-time-parse");
        Check(AgentTime.ParseDateTime(20261104, 0) == new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Unspecified), "ws9-time-midnight");
        Check(AgentTime.ParseDateTime(0, 0) is null && AgentTime.ParseDateTime(20261340, 0) is null && AgentTime.ParseDateTime(20261104, 246100) is null, "ws9-time-invalid-null");
        Check(AgentTime.ParseDuration(12345) == new TimeSpan(1, 23, 45) && AgentTime.ParseDuration(1000000) == TimeSpan.FromHours(100) && AgentTime.ParseDuration(7) == TimeSpan.FromSeconds(7), "ws9-duration-parse");
        Check(AgentTime.ParseDuration(-1) is null && AgentTime.ParseDuration(6100) is null, "ws9-duration-invalid-null");
        Check(AgentTime.Format(new DateTime(2026, 3, 8, 2, 30, 0)) == "2026-03-08 02:30:00 (server time)" && AgentTime.Format(null) == "—", "ws9-time-format-server-time");

        Check(AgentSchedules.Describe(4, 1, 0, 0, 90000).Contains("Every day", StringComparison.Ordinal) && AgentSchedules.Describe(4, 3, 0, 0, 90000).Contains("Every 3 days", StringComparison.Ordinal), "ws9-schedule-daily");
        var weekly = AgentSchedules.Describe(8, 2 | 8, 1, 0, 0, recurrence: 1);
        Check(weekly.Contains("Mon", StringComparison.Ordinal) && weekly.Contains("Wed", StringComparison.Ordinal) && weekly.Contains("every week", StringComparison.OrdinalIgnoreCase) && !weekly.Contains("Tue", StringComparison.Ordinal), "ws9-schedule-weekly-days");
        Check(AgentSchedules.Describe(4, 1, 4, 15, 0).Contains("every 15 minutes", StringComparison.Ordinal), "ws9-schedule-subday");
        Check(AgentSchedules.Describe(64, 0, 0, 0, 0).Contains("starts", StringComparison.OrdinalIgnoreCase) && AgentSchedules.Describe(999, 0, 0, 0, 0).Contains("Unrecognised", StringComparison.Ordinal), "ws9-schedule-kinds");
        Check(AgentStatuses.FromRunStatus(1) == AgentRunStatus.Succeeded && AgentStatuses.FromRunStatus(0) == AgentRunStatus.Failed
              && AgentStatuses.FromRunStatus(4) == AgentRunStatus.InProgress && AgentStatuses.FromRunStatus(77) == AgentRunStatus.Unknown, "ws9-status-map");
        Pass("ws9-agent-time-text");
    }

    // ---- fake runners ------------------------------------------------------------------------------------------

    sealed class FakeAgentQueries : IAgentQueryRunner
    {
        public List<(string Sql, IReadOnlyList<EditParameter> Parameters, int MaxRows)> Calls { get; } = [];
        public Func<string, RawRows>? Respond { get; set; }
        public Exception? Failure { get; set; }

        public Task<RawRows> QueryAsync(string sql, IReadOnlyList<EditParameter> parameters, int maxRows, CancellationToken cancellationToken)
        {
            Calls.Add((sql, parameters, maxRows));
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(Respond?.Invoke(sql) ?? new RawRows([], [], [], false));
        }
    }

    sealed class FakeAgentCommands : IAgentCommandRunner
    {
        public List<AgentCommand> Executed { get; } = [];
        public Exception? Failure { get; set; }

        public Task ExecuteAsync(AgentCommand command, CancellationToken cancellationToken)
        {
            Executed.Add(command);
            if (Failure is not null)
                throw Failure;
            return Task.CompletedTask;
        }
    }

    static RawRows Rows(params object?[][] rows) => new([], [], rows, false);

    static readonly Guid JobA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid JobB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    static async Task CatalogReads()
    {
        var q = new FakeAgentQueries
        {
            Respond = sql =>
            {
                if (sql.Contains("sysjobhistory", StringComparison.Ordinal))
                    return Rows(
                        [101L, 0, "(Job outcome)", 0, 20261104, 93005, 12345, "The job failed.  password=Hunter2 Step 1 failed\r\nline2"],
                        [100L, 1, "Step one", 1, 20261104, 93000, 5, "ok"]);
                if (sql.Contains("sysschedules", StringComparison.Ordinal))
                    return Rows(["Nightly", 1, 4, 1, 0, 0, 1, 20000, 20261105, 20000], ["Broken", 0, 4, 1, 0, 0, 0, 0, 0, 0]);
                return Rows(
                    [JobA, "Nightly backup", 1, "desc", 1, 20261103, 20000, 20261105, 20000],
                    [JobB, "Disabled job", 0, DBNull.Value, 5, 0, 0, 0, 0],
                    ["not-a-guid", "bad", 1, DBNull.Value, 1, 0, 0, 0, 0]);
            }
        };

        var jobs = await AgentCatalog.ListJobsAsync(q);
        Check(jobs.Failure is null && jobs.Items.Count == 2 && jobs.Items[0].JobId == JobA && jobs.Items[0].Enabled && !jobs.Items[1].Enabled, "ws9-jobs-parsed-skips-bad-row");
        Check(jobs.Items[0].LastStatus == AgentRunStatus.Succeeded && jobs.Items[0].LastRun == new DateTime(2026, 11, 3, 2, 0, 0) && jobs.Items[0].NextRun == new DateTime(2026, 11, 5, 2, 0, 0), "ws9-jobs-times");
        Check(jobs.Items[1].LastRun is null && jobs.Items[1].NextRun is null && jobs.Items[1].LastStatus == AgentRunStatus.Unknown, "ws9-jobs-never-run-null");
        Check(q.Calls[0].MaxRows == AgentCatalog.MaxJobs && q.Calls[0].Sql.Contains($"TOP ({AgentCatalog.MaxJobs + 1})", StringComparison.Ordinal), "ws9-jobs-bounded");

        var schedules = await AgentCatalog.ListSchedulesAsync(q, JobA);
        Check(schedules.Items.Count == 2 && schedules.Items[0].Name == "Nightly" && schedules.Items[0].Enabled && !schedules.Items[1].Enabled && schedules.Items[1].NextRun is null, "ws9-schedules-parsed");
        var sc = q.Calls[^1];
        Check(sc.Sql.Contains("@job_id", StringComparison.Ordinal) && !sc.Sql.Contains(JobA.ToString(), StringComparison.OrdinalIgnoreCase)
              && sc.Parameters.Count == 1 && sc.Parameters[0].Name == "@job_id" && Equals(sc.Parameters[0].Value, JobA), "ws9-schedules-parameterised");

        var history = await AgentCatalog.ListHistoryAsync(q, JobA);
        Check(history.Items.Count == 2 && history.Items[0].Status == AgentRunStatus.Failed && history.Items[0].StepId == 0 && history.Items[1].Status == AgentRunStatus.Succeeded, "ws9-history-parsed");
        Check(history.Items[0].RunTime == new DateTime(2026, 11, 4, 9, 30, 5) && history.Items[0].Duration == new TimeSpan(1, 23, 45), "ws9-history-time-duration");
        Check(!history.Items[0].Message.Contains("Hunter2", StringComparison.Ordinal) && !history.Items[0].Message.Contains('\n'), "ws9-history-message-redacted-single-line");
        Check(q.Calls[^1].MaxRows == AgentCatalog.MaxHistory && q.Calls[^1].Sql.Contains("ORDER BY h.instance_id DESC", StringComparison.Ordinal) && q.Calls[^1].Sql.Contains("@job_id", StringComparison.Ordinal), "ws9-history-bounded-newest-first");

        // Every read is SELECT-only against msdb; there is no write verb anywhere in the catalog SQL.
        foreach (var call in q.Calls)
        {
            Check(call.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && call.Sql.Contains("msdb.dbo.", StringComparison.Ordinal)
                  && !Regex.IsMatch(call.Sql, @"\b(INSERT|UPDATE|DELETE|EXEC|EXECUTE|DROP|ALTER|CREATE|sp_)\b", RegexOptions.IgnoreCase), "ws9-catalog-sql-read-only");
        }

        // A truncated read says so; a job history message of absurd size is bounded.
        var big = new FakeAgentQueries { Respond = _ => new RawRows([], [], [[100L, 1, "s", 1, 20261104, 93000, 5, new string('x', 5000)]], true) };
        var bigHistory = await AgentCatalog.ListHistoryAsync(big, JobA);
        Check(bigHistory.Truncated && bigHistory.Items[0].Message.Length <= AgentCatalog.MaxMessageChars + 1, "ws9-history-truncation-and-message-bound");
        var omitted = new FakeAgentQueries { Respond = _ => new RawRows([], [], [[100L, 1, "s", 1, 20261104, 93000, 5, new RawCellOmitted("too large", "abc", 99999)]], false) };
        Check((await AgentCatalog.ListHistoryAsync(omitted, JobA)).Items[0].Message.Contains("too large", StringComparison.Ordinal), "ws9-history-omitted-cell-named");

        var empty = await AgentCatalog.ListJobsAsync(new FakeAgentQueries());
        Check(empty.Failure is null && empty.Items.Count == 0 && !empty.Truncated, "ws9-empty-is-ok-not-failure");
        Pass("ws9-agent-catalog-reads");
    }

    static async Task Denial()
    {
        var denied = new FakeAgentQueries { Failure = new ProviderErrorException(229, "The SELECT permission was denied on the object 'sysjobs', database 'msdb', schema 'dbo'. Server=prod01;Password=Hunter2") };
        var jobs = await AgentCatalog.ListJobsAsync(denied);
        Check(jobs.Items.Count == 0 && jobs.Failure is { Kind: CatalogFailureKind.Denied, Number: 229 }, "ws9-denied-mapped");
        Check(jobs.Failure!.Text.Contains("SQLAgent", StringComparison.Ordinal) && jobs.Failure.Text.Contains("msdb", StringComparison.Ordinal)
              && !jobs.Failure.Text.Contains("Hunter2", StringComparison.Ordinal) && !jobs.Failure.Text.Contains('\n'), "ws9-denied-text-agent-roles-redacted");
        Check(jobs.Failure.Kind != CatalogFailureKind.Error && jobs.Items.Count == 0, "ws9-denied-differs-from-empty");
        Check((await AgentCatalog.ListHistoryAsync(denied, JobA)).Failure?.Kind == CatalogFailureKind.Denied
              && (await AgentCatalog.ListSchedulesAsync(denied, JobA)).Failure?.Kind == CatalogFailureKind.Denied, "ws9-denied-history-and-schedules");

        var broken = await AgentCatalog.ListJobsAsync(new FakeAgentQueries { Failure = new InvalidOperationException("boom") });
        Check(broken.Failure is { Kind: CatalogFailureKind.Error }, "ws9-other-failure-is-error");
        var cancelled = await AgentCatalog.ListJobsAsync(new FakeAgentQueries { Failure = new OperationCanceledException() });
        Check(cancelled.Failure is { Kind: CatalogFailureKind.Cancelled }, "ws9-cancel-mapped");
        Pass("ws9-agent-denial");
    }

    static void Gate()
    {
        var yes = AgentCatalog.Gate(new ConnectionCapabilities { SupportsAgent = true, Probed = true });
        Check(yes.Availability == AgentAvailability.Available && yes.CanRead && yes.CanChange, "ws9-gate-available");
        var no = AgentCatalog.Gate(new ConnectionCapabilities { SupportsAgent = false, EngineEdition = 5, Probed = true, Notes = ["This engine edition does not include SQL Server Agent."] });
        Check(no.Availability == AgentAvailability.Unavailable && !no.CanRead && !no.CanChange && no.Message.Contains("SQL Server Agent", StringComparison.Ordinal), "ws9-gate-disabled-agent");
        var unknown = AgentCatalog.Gate(ConnectionCapabilities.Unprobed("16.0"));
        Check(unknown.Availability == AgentAvailability.Unknown && unknown.CanRead && !unknown.CanChange && unknown.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase), "ws9-gate-unknown-read-only");
        Pass("ws9-agent-gate");
    }

    static async Task Actions()
    {
        var start = AgentCommand.StartJob(JobA, "Nightly\nbackup");
        var delete = AgentCommand.DeleteJob(JobA, "Nightly backup");
        Check(start.Sql == "EXEC msdb.dbo.sp_start_job @job_id = @job_id;" && delete.Sql == "EXEC msdb.dbo.sp_delete_job @job_id = @job_id;", "ws9-command-sql-fixed");
        Check(start.Parameters.Count == 1 && Equals(start.Parameters[0].Value, JobA) && !start.Sql.Contains("Nightly", StringComparison.Ordinal) && !start.Preview.Contains('\n'), "ws9-command-parameterised-job-id");
        Check(delete.Preview.Contains("permanently", StringComparison.OrdinalIgnoreCase) && delete.Preview.Contains("history", StringComparison.OrdinalIgnoreCase) && delete.Preview.Contains(JobA.ToString(), StringComparison.OrdinalIgnoreCase), "ws9-delete-preview-states-effect");

        var available = AgentCatalog.Gate(new ConnectionCapabilities { SupportsAgent = true, Probed = true });
        var runner = new FakeAgentCommands();

        // Not confirmed: nothing is sent.
        var unconfirmed = await AgentActions.ExecuteAsync(runner, available, start, confirmed: false);
        Check(unconfirmed.Outcome == AgentActionOutcome.Rejected && runner.Executed.Count == 0, "ws9-action-requires-confirmation");
        // Agent unavailable / unknown: nothing is sent even when confirmed.
        var blocked = await AgentActions.ExecuteAsync(runner, AgentCatalog.Gate(new ConnectionCapabilities { SupportsAgent = false }), delete, confirmed: true);
        var unknownGate = await AgentActions.ExecuteAsync(runner, AgentCatalog.Gate(ConnectionCapabilities.Unprobed(null)), delete, confirmed: true);
        Check(blocked.Outcome == AgentActionOutcome.Rejected && unknownGate.Outcome == AgentActionOutcome.Rejected && runner.Executed.Count == 0, "ws9-action-gated-when-agent-unavailable");

        var done = await AgentActions.ExecuteAsync(runner, available, start, confirmed: true);
        Check(done.Outcome == AgentActionOutcome.Done && runner.Executed.Count == 1 && runner.Executed[0] == start && done.Message.Contains("history", StringComparison.OrdinalIgnoreCase), "ws9-action-run-confirmed");
        var deleted = await AgentActions.ExecuteAsync(runner, available, delete, confirmed: true);
        Check(deleted.Outcome == AgentActionOutcome.Done && runner.Executed.Count == 2 && runner.Executed[1].Kind == AgentCommandKind.DeleteJob && runner.Executed[1].JobId == JobA, "ws9-action-delete-exact-target");

        // Denied role, step failure (server error) and the unknown-outcome paths are distinct.
        var denied = await AgentActions.ExecuteAsync(new FakeAgentCommands { Failure = new ProviderErrorException(229, "EXECUTE permission was denied on 'sp_start_job'") }, available, start, true);
        Check(denied.Outcome == AgentActionOutcome.Denied && denied.Message.Contains("SQLAgent", StringComparison.Ordinal), "ws9-action-denied-role");
        var failed = await AgentActions.ExecuteAsync(new FakeAgentCommands { Failure = new ProviderErrorException(22022, "SQLServerAgent is not currently running\nso it cannot be notified") }, available, start, true);
        Check(failed.Outcome == AgentActionOutcome.Failed && !failed.Message.Contains('\n') && failed.Message.Contains("not currently running", StringComparison.Ordinal), "ws9-action-server-error-failed");
        var timeout = await AgentActions.ExecuteAsync(new FakeAgentCommands { Failure = new ProviderErrorException(-2, "Execution Timeout Expired") }, available, delete, true);
        var cancel = await AgentActions.ExecuteAsync(new FakeAgentCommands { Failure = new OperationCanceledException() }, available, delete, true);
        var lost = await AgentActions.ExecuteAsync(new FakeAgentCommands { Failure = new ProviderErrorException(-1, "connection broken") }, available, delete, true);
        Check(timeout.Outcome == AgentActionOutcome.OutcomeUnknown && cancel.Outcome == AgentActionOutcome.OutcomeUnknown && lost.Outcome == AgentActionOutcome.OutcomeUnknown, "ws9-action-timeout-outcome-unknown");
        Check(timeout.Message.Contains("may", StringComparison.OrdinalIgnoreCase) && timeout.Message.Contains("history", StringComparison.OrdinalIgnoreCase), "ws9-action-unknown-points-to-server-history");
        Pass("ws9-agent-actions");
    }

    // ---- app wiring --------------------------------------------------------------------------------------------

    static void AppWiring()
    {
        var root = FindRepoRoot();
        var app = Path.Combine(root, "src", "Sqloryn.App");
        Check(File.Exists(Path.Combine(app, "AgentWindow.axaml")) && File.Exists(Path.Combine(app, "AgentWindow.axaml.cs")), "ws9-agent-window-exists");
        var window = File.ReadAllText(Path.Combine(app, "AgentWindow.axaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(app, "AgentWindow.axaml"));

        Check(!Regex.IsMatch(window, @"\b(EXEC|msdb|sp_start_job|sp_delete_job|SELECT\s)", RegexOptions.None), "ws9-agent-window-no-raw-sql");
        Check(window.Contains("AgentCatalog.Gate", StringComparison.Ordinal) && window.Contains("CanChange", StringComparison.Ordinal), "ws9-agent-window-gated-by-capabilities");
        Check(window.Contains("ListJobsAsync", StringComparison.Ordinal) && window.Contains("ListHistoryAsync", StringComparison.Ordinal) && window.Contains("ListSchedulesAsync", StringComparison.Ordinal), "ws9-agent-window-reads-via-core");

        // Run and Delete: exactly one execute call site each, each preceded by an explicit confirmation dialog.
        Check(window.Split("AgentActions.ExecuteAsync").Length == 3, "ws9-agent-window-two-write-call-sites");
        foreach (var (handler, ctor) in new[] { ("OnRunJob", "AgentCommand.StartJob"), ("OnDeleteJob", "AgentCommand.DeleteJob") })
        {
            var start = window.IndexOf("async void " + handler, StringComparison.Ordinal);
            Check(start > 0, "ws9-agent-window-handler-" + handler);
            var next = window.IndexOf("\n    async void ", start + 10, StringComparison.Ordinal);
            var body = next < 0 ? window[start..] : window[start..next];
            var confirm = body.IndexOf("Dialogs.ConfirmAsync", StringComparison.Ordinal);
            var exec = body.IndexOf("AgentActions.ExecuteAsync", StringComparison.Ordinal);
            Check(confirm > 0 && exec > confirm && body.Contains(ctor, StringComparison.Ordinal) && body.Contains("confirmed: true", StringComparison.Ordinal), "ws9-agent-" + handler + "-confirms-before-execute");
            Check(body.Contains(".Preview", StringComparison.Ordinal), "ws9-agent-" + handler + "-shows-preview");
        }
        Check(window.Contains("destructive: true", StringComparison.Ordinal), "ws9-agent-delete-marked-destructive");
        Check(xaml.Contains("RunButton", StringComparison.Ordinal) && xaml.Contains("DeleteButton", StringComparison.Ordinal) && xaml.Contains("HistoryGrid", StringComparison.Ordinal) && xaml.Contains("JobsGrid", StringComparison.Ordinal), "ws9-agent-window-list-and-history");

        var shell = File.ReadAllText(Path.Combine(app, "MainWindow.Shell.cs"));
        var main = File.ReadAllText(Path.Combine(app, "MainWindow.axaml.cs"));
        Check(shell.Contains("OnOpenAgent", StringComparison.Ordinal) && main.Contains("new AgentWindow(", StringComparison.Ordinal), "ws9-agent-reachable-from-menu");

        var session = File.ReadAllText(Path.Combine(root, "src", "Sqloryn.Core", "SqlSession.cs"));
        Check(session.Contains("ExecuteAgentCommandAsync", StringComparison.Ordinal) && session.Contains("command.Sql", StringComparison.Ordinal), "ws9-session-agent-command-seam");
        Pass("ws9-agent-app-wiring");
    }
}
