using Sqloryn.Core;

/// <summary>
/// WS8-T1 operations: backup/restore plan, DBCC CHECKDB plan, preview-never-executes and honest outcomes.
/// Component-only: no SQL Server connection. A live BACKUP/RESTORE round trip needs a disposable fixture and is not claimed.
/// </summary>
static class OperationsTests
{
    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"FAIL {name}");
    }

    static void Pass(string name) => Console.WriteLine($"PASS {name}");

    static readonly BackupDevice ServerFile = new("/var/opt/mssql/backup/app.bak", BackupPathKind.ServerSide);

    static BackupRestorePlan Backup() => new()
    {
        Operation = OperationKind.Backup,
        Database = "AppDb",
        Device = ServerFile
    };

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    static QueryResult Result(QueryCompletionKind kind, string? error = null, TargetConfidence target = TargetConfidence.Known) => new()
    {
        ExecutedSql = "x",
        Server = "s",
        Database = "master",
        Completion = kind,
        ErrorSummary = error,
        TargetConfidence = target
    };

    public static async Task RunAsync()
    {
        // ---- path labelling ----------------------------------------------------------------
        Check(ServerFile.Label.Contains("Server-side", StringComparison.Ordinal)
              && ServerFile.Label.Contains("SQL Server", StringComparison.Ordinal), "device-label-server");
        var mac = new BackupDevice("/Users/me/Desktop/app.bak", BackupPathKind.LocalMac);
        Check(mac.Label.Contains("Local Mac", StringComparison.Ordinal)
              && mac.Label.Contains("cannot", StringComparison.OrdinalIgnoreCase), "device-label-local");
        Check(ServerFile.Label != mac.Label, "device-labels-distinct");
        Check(ServerFile.Describe().StartsWith(ServerFile.Label, StringComparison.Ordinal)
              && ServerFile.Describe().EndsWith(ServerFile.Path, StringComparison.Ordinal), "device-describe");

        var localPlan = new BackupRestorePlan { Operation = OperationKind.Backup, Database = "AppDb", Device = mac };
        Check(localPlan.Validate().Any(i => i.Severity == OperationIssueSeverity.Error && i.Message.Contains("Local Mac", StringComparison.Ordinal)),
            "local-mac-path-rejected-by-default");
        Check(Throws<InvalidOperationException>(() => localPlan.GeneratePreviewSql()), "local-mac-path-no-sql");
        var sameHost = new BackupRestorePlan { Operation = OperationKind.Backup, Database = "AppDb", Device = mac, ServerRunsOnThisMac = true };
        var sameHostIssues = sameHost.Validate();
        Check(sameHostIssues.All(i => i.Severity != OperationIssueSeverity.Error)
              && sameHostIssues.Any(i => i.Severity == OperationIssueSeverity.Warning && i.Message.Contains("Docker", StringComparison.Ordinal)),
            "local-mac-path-explicit-same-host-warns");
        var macLooking = new BackupRestorePlan
        {
            Operation = OperationKind.Backup, Database = "AppDb",
            Device = new BackupDevice("/Users/me/x.bak", BackupPathKind.ServerSide)
        };
        Check(macLooking.Validate().Any(i => i.Severity == OperationIssueSeverity.Warning && i.Message.Contains("looks like a Mac path", StringComparison.Ordinal)),
            "server-labelled-mac-looking-path-warns");
        Check(!new BackupRestorePlan { Operation = OperationKind.Backup, Database = "AppDb", Device = ServerFile }
            .Validate().Any(), "clean-server-path-no-issues");
        Pass("operations-path-labelling");

        // ---- backup SQL ----------------------------------------------------------------------
        var full = Backup().GeneratePreviewSql();
        Check(full.Contains("BACKUP DATABASE [AppDb]", StringComparison.Ordinal)
              && full.Contains("TO DISK = N'/var/opt/mssql/backup/app.bak'", StringComparison.Ordinal)
              && full.Contains("NOINIT", StringComparison.Ordinal)
              && !full.Contains("WITH INIT", StringComparison.Ordinal)
              && !full.Contains("DIFFERENTIAL", StringComparison.Ordinal), "backup-full-sql");
        var overwritten = new BackupRestorePlan { Operation = OperationKind.Backup, Database = "AppDb", Device = ServerFile, Overwrite = true };
        var ow = overwritten.GeneratePreviewSql();
        Check(ow.Contains("WITH INIT", StringComparison.Ordinal) && !ow.Contains("NOINIT", StringComparison.Ordinal) && overwritten.IsDestructive, "backup-overwrite-init-destructive");
        Check(!Backup().IsDestructive, "backup-append-not-destructive");
        var diff = new BackupRestorePlan
        {
            Operation = OperationKind.Backup, Database = "AppDb", Device = ServerFile,
            BackupKind = BackupKind.Differential, Compression = true, Checksum = true
        }.GeneratePreviewSql();
        Check(diff.Contains("DIFFERENTIAL", StringComparison.Ordinal) && diff.Contains("COMPRESSION", StringComparison.Ordinal)
              && diff.Contains("CHECKSUM", StringComparison.Ordinal), "backup-differential-options");
        var log = new BackupRestorePlan
        {
            Operation = OperationKind.Backup, Database = "AppDb", Device = ServerFile, BackupKind = BackupKind.Log, CopyOnly = true
        }.GeneratePreviewSql();
        Check(log.Contains("BACKUP LOG [AppDb]", StringComparison.Ordinal) && log.Contains("COPY_ONLY", StringComparison.Ordinal), "backup-log-copyonly");
        Check(Throws<InvalidOperationException>(() => new BackupRestorePlan
        {
            Operation = OperationKind.Backup, Database = "AppDb", Device = ServerFile, BackupKind = BackupKind.Differential, CopyOnly = true
        }.GeneratePreviewSql()), "backup-differential-copyonly-invalid");
        Check(Throws<InvalidOperationException>(() => new BackupRestorePlan
        {
            Operation = OperationKind.Backup, Database = "tempdb", Device = ServerFile
        }.GeneratePreviewSql()), "backup-tempdb-rejected");
        Pass("operations-backup-sql");

        // ---- hostile names / paths -----------------------------------------------------------
        const string hostile = "x]; DROP DATABASE master; --";
        var hostileBackup = new BackupRestorePlan { Operation = OperationKind.Backup, Database = hostile, Device = ServerFile }.GeneratePreviewSql();
        Check(hostileBackup.Contains("[x]]; DROP DATABASE master; --]", StringComparison.Ordinal)
              && !hostileBackup.Contains("DATABASE [x];", StringComparison.Ordinal), "hostile-db-backup-quoted");
        var hostileRestore = new BackupRestorePlan
        {
            Operation = OperationKind.Restore, Database = hostile, Device = ServerFile, TargetDatabaseExists = false
        }.GeneratePreviewSql();
        Check(hostileRestore.Contains("RESTORE DATABASE [x]]; DROP DATABASE master; --]", StringComparison.Ordinal), "hostile-db-restore-quoted");
        var hostilePath = new BackupRestorePlan
        {
            Operation = OperationKind.Backup, Database = "AppDb",
            Device = new BackupDevice("/b/o'; DROP DATABASE x; --.bak", BackupPathKind.ServerSide)
        }.GeneratePreviewSql();
        Check(hostilePath.Contains("N'/b/o''; DROP DATABASE x; --.bak'", StringComparison.Ordinal), "hostile-path-literal-escaped");
        var hostileMove = new BackupRestorePlan
        {
            Operation = OperationKind.Restore, Database = "AppDb", Device = ServerFile, TargetDatabaseExists = false,
            Moves = [new RestoreFileMove("L'x", "/d/y'z.mdf")]
        }.GeneratePreviewSql();
        Check(hostileMove.Contains("MOVE N'L''x' TO N'/d/y''z.mdf'", StringComparison.Ordinal), "hostile-move-literals-escaped");
        foreach (var bad in new[] { "", "a\0b", new string('d', 129) })
            Check(Throws<InvalidOperationException>(() => new BackupRestorePlan { Operation = OperationKind.Backup, Database = bad, Device = ServerFile }.GeneratePreviewSql()),
                "invalid-db-name-rejected");
        foreach (var bad in new[] { "", "  ", "/a\nb.bak", "/a\0.bak", "https://acct.blob.core.windows.net/c/a.bak", new string('p', 260) })
            Check(new BackupRestorePlan { Operation = OperationKind.Backup, Database = "AppDb", Device = new BackupDevice(bad, BackupPathKind.ServerSide) }
                .Validate().Any(i => i.Severity == OperationIssueSeverity.Error), "invalid-device-path-rejected");
        Pass("operations-hostile-input");

        // ---- restore -----------------------------------------------------------------------
        var restoreExisting = new BackupRestorePlan
        {
            Operation = OperationKind.Restore, Database = "AppDb", Device = ServerFile, TargetDatabaseExists = true
        };
        Check(restoreExisting.Validate().Any(i => i.Severity == OperationIssueSeverity.Error && i.Message.Contains("overwrite", StringComparison.OrdinalIgnoreCase)),
            "restore-existing-requires-overwrite");
        Check(Throws<InvalidOperationException>(() => restoreExisting.GeneratePreviewSql()), "restore-existing-no-sql-without-flag");
        var overwrite = new BackupRestorePlan
        {
            Operation = OperationKind.Restore, Database = "AppDb", Device = ServerFile, TargetDatabaseExists = true, Overwrite = true
        };
        var overwriteSql = overwrite.GeneratePreviewSql();
        Check(overwriteSql.Contains("RESTORE DATABASE [AppDb]", StringComparison.Ordinal)
              && overwriteSql.Contains("FROM DISK = N'/var/opt/mssql/backup/app.bak'", StringComparison.Ordinal)
              && overwriteSql.Contains("REPLACE", StringComparison.Ordinal) && overwrite.IsDestructive, "restore-overwrite-replace-destructive");
        var fresh = new BackupRestorePlan { Operation = OperationKind.Restore, Database = "AppDb", Device = ServerFile, TargetDatabaseExists = false };
        var freshSql = fresh.GeneratePreviewSql();
        Check(!freshSql.Contains("REPLACE", StringComparison.Ordinal) && freshSql.Contains("RECOVERY", StringComparison.Ordinal) && !fresh.IsDestructive,
            "restore-new-no-replace");
        var unknownTarget = new BackupRestorePlan { Operation = OperationKind.Restore, Database = "AppDb", Device = ServerFile };
        Check(unknownTarget.Validate().Any(i => i.Severity == OperationIssueSeverity.Warning && i.Message.Contains("not checked", StringComparison.OrdinalIgnoreCase))
              && !unknownTarget.GeneratePreviewSql().Contains("REPLACE", StringComparison.Ordinal), "restore-unknown-target-warns-no-replace");
        Check(Throws<InvalidOperationException>(() => new BackupRestorePlan
        {
            Operation = OperationKind.Restore, Database = "master", Device = ServerFile, TargetDatabaseExists = true, Overwrite = true
        }.GeneratePreviewSql()), "restore-system-db-rejected");

        string RestoreWith(RestoreRecoveryMode mode, string? standby = null) => new BackupRestorePlan
        {
            Operation = OperationKind.Restore, Database = "AppDb", Device = ServerFile, TargetDatabaseExists = false,
            Recovery = mode, StandbyUndoPath = standby
        }.GeneratePreviewSql();
        Check(RestoreWith(RestoreRecoveryMode.Recovery).Contains("WITH RECOVERY", StringComparison.Ordinal)
              || RestoreWith(RestoreRecoveryMode.Recovery).Contains(", RECOVERY", StringComparison.Ordinal), "restore-recovery");
        Check(RestoreWith(RestoreRecoveryMode.NoRecovery).Contains("NORECOVERY", StringComparison.Ordinal), "restore-norecovery");
        Check(RestoreWith(RestoreRecoveryMode.Standby, "/var/opt/mssql/undo.bak").Contains("STANDBY = N'/var/opt/mssql/undo.bak'", StringComparison.Ordinal), "restore-standby");
        Check(Throws<InvalidOperationException>(() => RestoreWith(RestoreRecoveryMode.Standby)), "restore-standby-needs-undo-path");
        var moved = new BackupRestorePlan
        {
            Operation = OperationKind.Restore, Database = "Copy", Device = ServerFile, TargetDatabaseExists = false,
            Moves = [new RestoreFileMove("AppDb", "/data/copy.mdf"), new RestoreFileMove("AppDb_log", "/data/copy.ldf")]
        }.GeneratePreviewSql();
        Check(moved.Contains("MOVE N'AppDb' TO N'/data/copy.mdf'", StringComparison.Ordinal)
              && moved.Contains("MOVE N'AppDb_log' TO N'/data/copy.ldf'", StringComparison.Ordinal), "restore-move-files");
        Check(new BackupRestorePlan
        {
            Operation = OperationKind.Restore, Database = "Copy", Device = ServerFile, TargetDatabaseExists = false,
            Moves = [new RestoreFileMove("a", "/data/x.mdf"), new RestoreFileMove("b", "/DATA/X.mdf")]
        }.Validate().Any(i => i.Severity == OperationIssueSeverity.Error && i.Message.Contains("same target", StringComparison.OrdinalIgnoreCase)), "restore-duplicate-move-target");
        var restoreLog = new BackupRestorePlan
        {
            Operation = OperationKind.Restore, Database = "AppDb", Device = ServerFile, TargetDatabaseExists = true,
            RestoreLog = true, Recovery = RestoreRecoveryMode.NoRecovery
        }.GeneratePreviewSql();
        Check(restoreLog.Contains("RESTORE LOG [AppDb]", StringComparison.Ordinal) && !restoreLog.Contains("REPLACE", StringComparison.Ordinal), "restore-log");
        Check(Throws<InvalidOperationException>(() => new BackupRestorePlan
        {
            Operation = OperationKind.Restore, Database = "AppDb", Device = ServerFile, TargetDatabaseExists = true,
            RestoreLog = true, Overwrite = true
        }.GeneratePreviewSql()), "restore-log-replace-invalid");
        Pass("operations-restore-sql");

        // ---- integrity ------------------------------------------------------------------------
        var check = new IntegrityCheckPlan("AppDb");
        var checkSql = check.GeneratePreviewSql();
        Check(checkSql.Contains("DBCC CHECKDB (N'AppDb')", StringComparison.Ordinal)
              && checkSql.Contains("NO_INFOMSGS", StringComparison.Ordinal) && checkSql.Contains("ALL_ERRORMSGS", StringComparison.Ordinal)
              && !checkSql.Contains("PHYSICAL_ONLY", StringComparison.Ordinal)
              && !checkSql.Contains("REPAIR", StringComparison.Ordinal) && !check.IsDestructive, "checkdb-sql-no-repair");
        Check(new IntegrityCheckPlan("AppDb", physicalOnly: true).GeneratePreviewSql().Contains("PHYSICAL_ONLY", StringComparison.Ordinal), "checkdb-physical-only");
        Check(new IntegrityCheckPlan("O'Brien]").GeneratePreviewSql().Contains("DBCC CHECKDB (N'O''Brien]')", StringComparison.Ordinal), "checkdb-hostile-name-literal");
        Check(Throws<InvalidOperationException>(() => new IntegrityCheckPlan("").GeneratePreviewSql()), "checkdb-empty-name-rejected");
        Check(Throws<InvalidOperationException>(() => new IntegrityCheckPlan("a\0b").GeneratePreviewSql()), "checkdb-nul-name-rejected");
        Pass("operations-checkdb");

        // ---- preview never executes; confirm gates execution -----------------------------------
        var calls = new List<string>();
        Task<QueryResult> Fake(string sql, CancellationToken _)
        {
            calls.Add(sql);
            return Task.FromResult(Result(QueryCompletionKind.Succeeded));
        }
        IOperationPlan plan = overwrite;
        var previewSql = plan.GeneratePreviewSql();
        _ = new IntegrityCheckPlan("AppDb").GeneratePreviewSql();
        _ = plan.Validate();
        Check(calls.Count == 0, "preview-does-not-execute");

        Check(await ThrowsAsync(() => OperationRunner.ExecuteAsync(plan, previewSql, confirmed: false, Fake, CancellationToken.None)), "execute-requires-confirm");
        Check(calls.Count == 0, "unconfirmed-does-not-execute");
        Check(await ThrowsAsync(() => OperationRunner.ExecuteAsync(plan, "stale previewed sql", confirmed: true, Fake, CancellationToken.None)), "execute-requires-matching-preview");
        Check(calls.Count == 0, "stale-preview-does-not-execute");
        Check(await ThrowsAsync(() => OperationRunner.ExecuteAsync(restoreExisting, "", confirmed: true, Fake, CancellationToken.None)), "execute-invalid-plan-refused");
        Check(calls.Count == 0, "invalid-plan-does-not-execute");
        var ran = await OperationRunner.ExecuteAsync(plan, previewSql, confirmed: true, Fake, CancellationToken.None);
        Check(calls.Count == 1 && calls[0] == previewSql && ran.Outcome == OperationOutcome.Completed, "confirmed-executes-exactly-previewed-sql-once");
        Pass("operations-preview-confirm-gate");

        // ---- honest outcomes ---------------------------------------------------------------------
        static Task<QueryResult> Returns(QueryResult r) => Task.FromResult(r);
        var failed = await OperationRunner.ExecuteAsync(plan, previewSql, true, (_, _) => Returns(Result(QueryCompletionKind.Failed, "Msg 3159")), CancellationToken.None);
        Check(failed.Outcome == OperationOutcome.Failed && failed.Message.Contains("Msg 3159", StringComparison.Ordinal), "outcome-failed");
        foreach (var kind in new[] { QueryCompletionKind.UnknownAfterDisconnect, QueryCompletionKind.TimedOut, QueryCompletionKind.CancelledClientEnded,
                     QueryCompletionKind.CancellationRequested, QueryCompletionKind.FinalCompletionUnobserved })
        {
            var r = await OperationRunner.ExecuteAsync(plan, previewSql, true, (_, _) => Returns(Result(kind)), CancellationToken.None);
            Check(r.Outcome == OperationOutcome.OutcomeUnknown
                  && r.Message.Contains("not proof", StringComparison.OrdinalIgnoreCase)
                  && r.Message.Contains("sys.dm_exec_requests", StringComparison.Ordinal), $"outcome-unknown-{kind}");
        }
        var unknownTargetRun = await OperationRunner.ExecuteAsync(plan, previewSql, true,
            (_, _) => Returns(Result(QueryCompletionKind.Succeeded, null, TargetConfidence.Unknown)), CancellationToken.None);
        Check(unknownTargetRun.Outcome == OperationOutcome.Completed && unknownTargetRun.Message.Contains("revalidate", StringComparison.OrdinalIgnoreCase), "outcome-unknown-target-noted");
        var verified = await OperationRunner.ExecuteAsync(plan, previewSql, true, (_, _) => Returns(Result(QueryCompletionKind.CancelledServerVerified)), CancellationToken.None);
        Check(verified.Outcome == OperationOutcome.Cancelled && verified.Message.Contains("verify", StringComparison.OrdinalIgnoreCase), "outcome-cancelled-verified");
        Check(OperationRunner.ProgressProbeSql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
              && OperationRunner.ProgressProbeSql.Contains("sys.dm_exec_requests", StringComparison.Ordinal)
              && !OperationRunner.ProgressProbeSql.Contains("KILL", StringComparison.OrdinalIgnoreCase), "progress-probe-read-only");
        Pass("operations-honest-outcomes");

        // ---- window wiring (static, no display needed) ---------------------------------------------
        var appDir = Path.Combine(FindRepoRoot(), "src", "Sqloryn.App");
        var window = File.ReadAllText(Path.Combine(appDir, "OperationsWindow.axaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(appDir, "OperationsWindow.axaml"));
        Check(window.Contains("OperationRunner.ExecuteAsync", StringComparison.Ordinal)
              && window.Contains("Dialogs.ConfirmAsync", StringComparison.Ordinal)
              && !window.Contains("BACKUP ", StringComparison.Ordinal) && !window.Contains("RESTORE ", StringComparison.Ordinal)
              && !window.Contains("DBCC ", StringComparison.Ordinal), "operations-window-sql-from-core-and-confirmed");
        Check(xaml.Contains("Backup", StringComparison.Ordinal) && xaml.Contains("Restore", StringComparison.Ordinal) && xaml.Contains("Check", StringComparison.Ordinal)
              && xaml.Contains("TabItem", StringComparison.Ordinal), "operations-window-has-three-tabs");
        Pass("operations-window-wiring");
    }

    static async Task<bool> ThrowsAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (InvalidOperationException) { return true; }
    }

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SQLoryn.slnx")))
                return d.FullName;
        throw new Exception("FAIL repo root (SQLoryn.slnx) not found from test base directory");
    }
}
