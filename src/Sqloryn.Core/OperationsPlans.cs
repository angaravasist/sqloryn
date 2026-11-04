using System.Text;

namespace Sqloryn.Core;

public enum OperationIssueSeverity { Error, Warning }

public sealed record OperationIssue(OperationIssueSeverity Severity, string Message);

public enum OperationKind { Backup, Restore }

public enum BackupKind { Full, Differential, Log }

public enum RestoreRecoveryMode
{
    /// <summary>Bring the database online (RECOVERY). No further backups can be restored.</summary>
    Recovery,

    /// <summary>Leave the database restoring so more backups can be applied (NORECOVERY).</summary>
    NoRecovery,

    /// <summary>Read-only access between log restores (STANDBY); needs a server-side undo file.</summary>
    Standby
}

/// <summary>Where a backup device path lives. SQL Server only ever reads/writes its own filesystem.</summary>
public enum BackupPathKind
{
    /// <summary>A path on the machine running SQL Server (the only place BACKUP/RESTORE can reach).</summary>
    ServerSide,

    /// <summary>A path on this Mac. A remote or containerised server cannot see it.</summary>
    LocalMac
}

/// <summary>A BACKUP TO / RESTORE FROM disk device with an explicit statement of whose filesystem it is on.</summary>
public sealed record BackupDevice(string Path, BackupPathKind Kind)
{
    public string Label => Kind == BackupPathKind.ServerSide
        ? "Server-side path (on the SQL Server host; SQL Server reads and writes it)"
        : "Local Mac path (this Mac; a remote or containerised SQL Server cannot access it)";

    /// <summary>Label plus the path with control characters made visible, safe for dialogs.</summary>
    public string Describe() => Label + ": " + OperationText.Visible(Path);
}

/// <summary>Maps one logical file in the backup to a server-side target path.</summary>
public sealed record RestoreFileMove(string LogicalName, string TargetPath);

/// <summary>Anything the Operations window previews and then (only after confirmation) executes.</summary>
public interface IOperationPlan
{
    string Title { get; }

    /// <summary>The plan overwrites or discards existing server-side data (REPLACE / INIT).</summary>
    bool IsDestructive { get; }

    /// <summary>Human summary for the confirmation dialog.</summary>
    string ConfirmationSummary { get; }

    IReadOnlyList<OperationIssue> Validate();

    /// <summary>Pure text generation. Throws <see cref="InvalidOperationException"/> when <see cref="Validate"/> has errors. Never touches a database.</summary>
    string GeneratePreviewSql();
}

static class OperationText
{
    public const int MaxDevicePathChars = 259;

    public static string Literal(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static bool HasControl(string value) => value.Any(char.IsControl);

    /// <summary>Control characters shown as \uXXXX so a hostile name/path cannot reshape a dialog or log line.</summary>
    public static string Visible(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(char.IsControl(c) ? $"\\u{(int)c:X4}" : c);
        return sb.ToString();
    }

    public static string? DatabaseNameProblem(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "Database name is required.";
        if (name.Length > 128)
            return "Database name is longer than 128 characters.";
        if (name.Contains('\0', StringComparison.Ordinal))
            return "Database name contains a NUL character.";
        return null;
    }

    public static bool IsSystemDatabase(string name)
        => name.Equals("master", StringComparison.OrdinalIgnoreCase)
           || name.Equals("model", StringComparison.OrdinalIgnoreCase)
           || name.Equals("msdb", StringComparison.OrdinalIgnoreCase)
           || name.Equals("tempdb", StringComparison.OrdinalIgnoreCase);

    public static string? ServerPathProblem(string? path, string what)
    {
        if (string.IsNullOrWhiteSpace(path))
            return $"{what} is required.";
        if (path.Length > MaxDevicePathChars)
            return $"{what} is longer than {MaxDevicePathChars} characters.";
        if (HasControl(path))
            return $"{what} contains control characters.";
        if (path.Contains("://", StringComparison.Ordinal))
            return $"{what} looks like a URL; backup to URL (credentials) is not supported here.";
        return null;
    }

    public static void ThrowIfErrors(IReadOnlyList<OperationIssue> issues)
    {
        var errors = issues.Where(i => i.Severity == OperationIssueSeverity.Error).Select(i => i.Message).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException("The operation plan has problems: " + string.Join(" ", errors));
    }
}

/// <summary>
/// One BACKUP or RESTORE, as structured inputs. All SQL is generated here: identifiers are bracket-quoted, paths and
/// logical names are escaped N'' literals. Nothing in this type contacts a server or the filesystem; paths are the
/// SQL Server host's, never opened by the client.
/// </summary>
public sealed class BackupRestorePlan : IOperationPlan
{
    public required OperationKind Operation { get; init; }
    public required string Database { get; init; }
    public required BackupDevice Device { get; init; }

    /// <summary>Backup: INIT (replace the backup sets already in the file). Restore: REPLACE (overwrite an existing database).</summary>
    public bool Overwrite { get; init; }

    /// <summary>The user states the server runs on this Mac, so a <see cref="BackupPathKind.LocalMac"/> path is meaningful.</summary>
    public bool ServerRunsOnThisMac { get; init; }

    public bool Checksum { get; init; } = true;

    // Backup
    public BackupKind BackupKind { get; init; } = BackupKind.Full;
    public bool CopyOnly { get; init; }
    public bool Compression { get; init; }

    // Restore
    public RestoreRecoveryMode Recovery { get; init; } = RestoreRecoveryMode.Recovery;

    /// <summary>Server-side undo file for <see cref="RestoreRecoveryMode.Standby"/>.</summary>
    public string? StandbyUndoPath { get; init; }

    /// <summary>Whether the destination database already exists, as read from the catalog (null = not checked).</summary>
    public bool? TargetDatabaseExists { get; init; }

    public bool RestoreLog { get; init; }
    public IReadOnlyList<RestoreFileMove> Moves { get; init; } = [];

    public string Title => Operation == OperationKind.Backup ? "Backup database" : "Restore database";

    public bool IsDestructive => Overwrite;

    public string ConfirmationSummary
    {
        get
        {
            var db = OperationText.Visible(Database);
            if (Operation == OperationKind.Backup)
                return $"Backs up database {db} to:\n{Device.Describe()}\n" +
                       (Overwrite
                           ? "OVERWRITES every backup set already stored in that file (INIT)."
                           : "Appends a new backup set to that file (NOINIT).");
            return $"Restores database {db} from:\n{Device.Describe()}\n" +
                   (Overwrite
                       ? "OVERWRITES the existing database with the same name (REPLACE). Its current data is lost."
                       : "Does not overwrite an existing database; the server rejects the restore if it exists.");
        }
    }

    public IReadOnlyList<OperationIssue> Validate()
    {
        var issues = new List<OperationIssue>();
        void Error(string m) => issues.Add(new OperationIssue(OperationIssueSeverity.Error, m));
        void Warn(string m) => issues.Add(new OperationIssue(OperationIssueSeverity.Warning, m));

        if (OperationText.DatabaseNameProblem(Database) is { } dbProblem)
            Error(dbProblem);

        var pathProblem = OperationText.ServerPathProblem(Device.Path, "Backup device path");
        if (pathProblem is not null)
            Error(pathProblem);
        else if (Device.Kind == BackupPathKind.LocalMac)
        {
            if (!ServerRunsOnThisMac)
                Error("Local Mac path: SQL Server reads and writes its own filesystem, so it cannot use a path on this Mac. Enter a path on the SQL Server host, or confirm that the server runs on this Mac.");
            else
                Warn("Local Mac path: this only works if SQL Server runs directly on this Mac. A Docker container or remote server has a different filesystem and will fail or write elsewhere.");
        }
        else if (Device.Path.StartsWith("/Users/", StringComparison.Ordinal) || Device.Path.StartsWith("/Volumes/", StringComparison.Ordinal)
                 || Device.Path.StartsWith("~/", StringComparison.Ordinal))
        {
            Warn("This server-side path looks like a Mac path (/Users, /Volumes or ~). Server-side paths are on the SQL Server host; check it is not a path on this Mac.");
        }

        if (Operation == OperationKind.Backup)
        {
            if (Database is { } b && b.Equals("tempdb", StringComparison.OrdinalIgnoreCase))
                Error("tempdb cannot be backed up.");
            if (BackupKind == BackupKind.Differential && CopyOnly)
                Error("A differential backup cannot be COPY_ONLY.");
            return issues;
        }

        if (Database is { Length: > 0 } r && OperationText.IsSystemDatabase(r))
            Error("Restoring a system database needs special handling (single-user / startup options) and is not offered here.");
        if (TargetDatabaseExists == true && !Overwrite && !RestoreLog)
            Error("The destination database already exists. Tick overwrite (REPLACE) to replace it, or restore under a different name.");
        else if (TargetDatabaseExists is null)
            Warn("Whether the destination database exists was not checked. Without overwrite the server rejects a restore over an existing database.");
        if (RestoreLog && Overwrite)
            Error("REPLACE does not apply to a log restore.");
        if (Recovery == RestoreRecoveryMode.Standby)
        {
            var standby = OperationText.ServerPathProblem(StandbyUndoPath, "Standby undo file path");
            if (standby is not null)
                Error(standby);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in Moves)
        {
            if (string.IsNullOrEmpty(move.LogicalName) || OperationText.HasControl(move.LogicalName) || move.LogicalName.Length > 128)
                Error("A file move has an invalid logical name.");
            if (OperationText.ServerPathProblem(move.TargetPath, "A file move target path") is { } moveProblem)
                Error(moveProblem);
            else if (!seen.Add(move.TargetPath))
                Error("Two files would be restored to the same target path.");
        }
        return issues;
    }

    public string GeneratePreviewSql()
    {
        OperationText.ThrowIfErrors(Validate());
        var db = SqlIdentity.QuoteIdentifier(Database);
        var device = OperationText.Literal(Device.Path);
        var options = new List<string>();

        if (Operation == OperationKind.Backup)
        {
            options.Add(Overwrite ? "INIT" : "NOINIT");
            if (BackupKind == BackupKind.Differential)
                options.Add("DIFFERENTIAL");
            if (CopyOnly)
                options.Add("COPY_ONLY");
            if (Compression)
                options.Add("COMPRESSION");
            if (Checksum)
                options.Add("CHECKSUM");
            options.Add("STATS = 10");
            var verb = BackupKind == BackupKind.Log ? "BACKUP LOG" : "BACKUP DATABASE";
            return $"{verb} {db}{Environment.NewLine}TO DISK = {device}{Environment.NewLine}WITH {string.Join(", ", options)};{Environment.NewLine}";
        }

        if (Overwrite)
            options.Add("REPLACE");
        foreach (var move in Moves)
            options.Add($"MOVE {OperationText.Literal(move.LogicalName)} TO {OperationText.Literal(move.TargetPath)}");
        options.Add(Recovery switch
        {
            RestoreRecoveryMode.NoRecovery => "NORECOVERY",
            RestoreRecoveryMode.Standby => "STANDBY = " + OperationText.Literal(StandbyUndoPath!),
            _ => "RECOVERY"
        });
        if (Checksum)
            options.Add("CHECKSUM");
        options.Add("STATS = 10");
        var restore = RestoreLog ? "RESTORE LOG" : "RESTORE DATABASE";
        return $"{restore} {db}{Environment.NewLine}FROM DISK = {device}{Environment.NewLine}WITH {string.Join(", ", options)};{Environment.NewLine}";
    }
}

/// <summary>DBCC CHECKDB preview. Read-only: no REPAIR_* option is ever generated.</summary>
public sealed class IntegrityCheckPlan(string database, bool physicalOnly = false, bool noInfoMessages = true) : IOperationPlan
{
    public string Database { get; } = database;
    public bool PhysicalOnly { get; } = physicalOnly;
    public bool NoInfoMessages { get; } = noInfoMessages;

    public string Title => "Check database integrity";
    public bool IsDestructive => false;

    public string ConfirmationSummary
        => $"Runs DBCC CHECKDB on {OperationText.Visible(Database)}" +
           (PhysicalOnly ? " (physical checks only)" : "") +
           ". Read-only, but it can load the server heavily and take a long time on a large database. No repair is attempted.";

    public IReadOnlyList<OperationIssue> Validate()
        => OperationText.DatabaseNameProblem(Database) is { } p
            ? [new OperationIssue(OperationIssueSeverity.Error, p)]
            : [];

    public string GeneratePreviewSql()
    {
        OperationText.ThrowIfErrors(Validate());
        var options = new List<string>();
        if (NoInfoMessages)
            options.Add("NO_INFOMSGS");
        options.Add("ALL_ERRORMSGS");
        if (PhysicalOnly)
            options.Add("PHYSICAL_ONLY");
        // Documented DBCC form takes the name as a string; the literal escapes quotes, so no name can end the statement.
        return $"DBCC CHECKDB ({OperationText.Literal(Database)}) WITH {string.Join(", ", options)};{Environment.NewLine}";
    }
}

public enum OperationOutcome
{
    Completed,
    Failed,

    /// <summary>The client observed a cancel; see the message for what that does and does not prove.</summary>
    Cancelled,

    /// <summary>The client lost sight of the operation. The server may still be running or may have finished.</summary>
    OutcomeUnknown
}

public sealed record OperationResult(OperationOutcome Outcome, string Message, QueryResult? Query);

public static class OperationRunner
{
    /// <summary>Read-only; run in a query tab to see BACKUP/RESTORE/DBCC progress that the server reports independently of this client.</summary>
    public const string ProgressProbeSql =
        "SELECT r.session_id, r.command, DB_NAME(r.database_id) AS database_name, r.status, r.percent_complete, r.start_time, r.estimated_completion_time " +
        "FROM sys.dm_exec_requests AS r " +
        "WHERE r.command LIKE N'BACKUP%' OR r.command LIKE N'RESTORE%' OR r.command LIKE N'DBCC%';";

    /// <summary>
    /// Executes <paramref name="plan"/> once, only when <paramref name="confirmed"/> and only the exact SQL the user previewed.
    /// Throws <see cref="InvalidOperationException"/> (without executing) otherwise.
    /// </summary>
    public static async Task<OperationResult> ExecuteAsync(
        IOperationPlan plan,
        string previewedSql,
        bool confirmed,
        Func<string, CancellationToken, Task<QueryResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(execute);
        if (!confirmed)
            throw new InvalidOperationException("The operation was not confirmed.");
        var sql = plan.GeneratePreviewSql(); // throws for an invalid plan
        if (!string.Equals(sql, previewedSql, StringComparison.Ordinal))
            throw new InvalidOperationException("The plan changed after it was previewed. Preview the SQL again.");

        var result = await execute(sql, cancellationToken).ConfigureAwait(false);
        return Describe(plan, result);
    }

    public static OperationResult Describe(IOperationPlan plan, QueryResult result)
    {
        var detail = (result.ErrorSummary is { Length: > 0 } e ? " " + e : "")
                     + (result.Messages.Count > 0 ? " " + string.Join(" ", result.Messages.Select(m => $"Msg {m.Number}: {m.Text}").Take(5)) : "");
        switch (result.Completion)
        {
            case QueryCompletionKind.Succeeded:
                var note = result.TargetConfidence == TargetConfidence.Unknown
                    ? " The session's database could not be verified afterwards; revalidate the target before running more SQL."
                    : "";
                return new OperationResult(OperationOutcome.Completed,
                    $"{plan.Title}: the server reported the statement completed.{note} Check the server (for example msdb.dbo.backupset) before relying on it.", result);
            case QueryCompletionKind.Failed:
                return new OperationResult(OperationOutcome.Failed, $"{plan.Title}: the server reported an error.{detail}", result);
            case QueryCompletionKind.CancelledServerVerified:
                return new OperationResult(OperationOutcome.Cancelled,
                    $"{plan.Title}: cancelled and the session state was verified. Verify the backup file or database state on the server before relying on it; partial output may exist.", result);
            default:
                return new OperationResult(OperationOutcome.OutcomeUnknown,
                    $"{plan.Title}: the outcome is UNKNOWN ({QueryOutcomeFormatter.DescribeStatus(result)}). A lost connection, timeout or cancel is not proof the operation stopped; " +
                    "SQL Server may still be running it or may have finished it. Do not re-run it blindly. Check progress and the result on the server, for example with the read-only query on sys.dm_exec_requests " +
                    $"and msdb.dbo.backupset.{detail}", result);
        }
    }
}
