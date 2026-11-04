using Avalonia.Controls;
using Avalonia.Interactivity;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>
/// Guided backup / restore / integrity-check sheet. Every statement comes from Core plans; previewing only generates
/// text (restore additionally reads the database list to learn whether the destination exists). Execution needs the
/// exact previewed SQL plus an explicit confirmation, and a lost connection is never reported as "stopped".
/// </summary>
public partial class OperationsWindow : Window
{
    readonly SqlSession _session;
    IOperationPlan? _previewedPlan;
    string? _previewedSql;
    bool _running;
    CancellationTokenSource? _cts;

    /// <summary>True once the server reported the operation completed or its outcome is unknown (state may have changed).</summary>
    public bool Applied { get; private set; }

    public OperationsWindow() : this(new SqlSession(), null)
    {
    }

    public OperationsWindow(SqlSession session, string? database)
    {
        _session = session;
        InitializeComponent();
        var name = database ?? session.Database ?? "";
        BackupDatabase.Text = name;
        RestoreDatabase.Text = name;
        CheckDatabase.Text = name;
        Closing += OnWindowClosing;
        WireInvalidation();
        StatusText.Text = "Preview the SQL first. Nothing runs, and nothing is read from the server's disks, until you confirm.";
    }

    // ---- invalidation: any edit after a preview makes the preview stale ------------------------

    void WireInvalidation()
    {
        foreach (var box in new[]
                 {
                     BackupDatabase, BackupPath, RestoreDatabase, RestorePath, RestoreStandbyPath, RestoreMoves, CheckDatabase
                 })
            box.TextChanged += (_, _) => Invalidate();
        foreach (var check in new[]
                 {
                     BackupOverwrite, BackupCopyOnly, BackupCompression, BackupChecksum, BackupServerOnMac,
                     RestoreOverwrite, RestoreLog, RestoreChecksum, RestoreServerOnMac, CheckPhysicalOnly
                 })
            check.IsCheckedChanged += (_, _) => Invalidate();
        foreach (var combo in new[] { BackupLocation, BackupType, RestoreLocation, RestoreRecovery })
            combo.SelectionChanged += (_, _) => Invalidate();
    }

    void Invalidate()
    {
        _previewedPlan = null;
        _previewedSql = null;
        if (ConfirmButton is not null)
            ConfirmButton.IsEnabled = false;
        if (SqlBox is not null)
            SqlBox.Text = "";
    }

    void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Tabs))
        {
            Invalidate();
            IssuesText.IsVisible = false;
        }
    }

    // ---- plan construction -------------------------------------------------------------------

    static BackupPathKind PathKind(ComboBox box) => box.SelectedIndex == 1 ? BackupPathKind.LocalMac : BackupPathKind.ServerSide;

    IOperationPlan BuildPlan(bool? restoreTargetExists)
    {
        switch (Tabs.SelectedIndex)
        {
            case 0:
                return new BackupRestorePlan
                {
                    Operation = OperationKind.Backup,
                    Database = (BackupDatabase.Text ?? "").Trim(),
                    Device = new BackupDevice((BackupPath.Text ?? "").Trim(), PathKind(BackupLocation)),
                    Overwrite = BackupOverwrite.IsChecked == true,
                    ServerRunsOnThisMac = BackupServerOnMac.IsChecked == true,
                    Checksum = BackupChecksum.IsChecked == true,
                    CopyOnly = BackupCopyOnly.IsChecked == true,
                    Compression = BackupCompression.IsChecked == true,
                    BackupKind = BackupType.SelectedIndex switch { 1 => BackupKind.Differential, 2 => BackupKind.Log, _ => BackupKind.Full }
                };
            case 1:
                return new BackupRestorePlan
                {
                    Operation = OperationKind.Restore,
                    Database = (RestoreDatabase.Text ?? "").Trim(),
                    Device = new BackupDevice((RestorePath.Text ?? "").Trim(), PathKind(RestoreLocation)),
                    Overwrite = RestoreOverwrite.IsChecked == true,
                    ServerRunsOnThisMac = RestoreServerOnMac.IsChecked == true,
                    Checksum = RestoreChecksum.IsChecked == true,
                    RestoreLog = RestoreLog.IsChecked == true,
                    Recovery = RestoreRecovery.SelectedIndex switch
                    {
                        1 => RestoreRecoveryMode.NoRecovery,
                        2 => RestoreRecoveryMode.Standby,
                        _ => RestoreRecoveryMode.Recovery
                    },
                    StandbyUndoPath = (RestoreStandbyPath.Text ?? "").Trim(),
                    TargetDatabaseExists = restoreTargetExists,
                    Moves = ParseMoves(RestoreMoves.Text)
                };
            default:
                return new IntegrityCheckPlan((CheckDatabase.Text ?? "").Trim(), CheckPhysicalOnly.IsChecked == true);
        }
    }

    static List<RestoreFileMove> ParseMoves(string? text)
    {
        var moves = new List<RestoreFileMove>();
        foreach (var raw in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0)
                continue;
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            moves.Add(eq < 0
                ? new RestoreFileMove(line, "")
                : new RestoreFileMove(line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return moves;
    }

    /// <summary>Read-only catalog lookup: does the restore destination already exist? Null when it cannot be told.</summary>
    async Task<bool?> LookupDestinationExistsAsync(string name)
    {
        if (!_session.IsConnected || string.IsNullOrEmpty(name))
            return null;
        try
        {
            var names = await _session.ListDatabasesAsync();
            return names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    // ---- preview (no execution) -----------------------------------------------------------------

    async void OnPreview(object? sender, RoutedEventArgs e)
    {
        if (_running)
            return;
        Invalidate();
        var tab = Tabs.SelectedIndex;
        bool? exists = null;
        if (tab == 1)
        {
            PreviewButton.IsEnabled = false;
            exists = await LookupDestinationExistsAsync((RestoreDatabase.Text ?? "").Trim());
            PreviewButton.IsEnabled = true;
            if (Tabs.SelectedIndex != tab)
                return;
        }

        var plan = BuildPlan(exists);
        var issues = plan.Validate();
        ShowIssues(issues);
        if (issues.Any(i => i.Severity == OperationIssueSeverity.Error))
        {
            StatusText.Text = "The plan has problems (see above). No SQL was generated and nothing was executed.";
            return;
        }

        _previewedSql = plan.GeneratePreviewSql();
        _previewedPlan = plan;
        SqlBox.Text = _previewedSql;
        ConfirmButton.IsEnabled = true;
        StatusText.Text = plan.IsDestructive
            ? "SQL preview ready. This operation OVERWRITES existing data. Nothing has been executed."
            : "SQL preview ready. Nothing has been executed.";
    }

    void ShowIssues(IReadOnlyList<OperationIssue> issues)
    {
        IssuesText.IsVisible = issues.Count > 0;
        IssuesText.Text = string.Join("\n", issues.Select(i =>
            (i.Severity == OperationIssueSeverity.Error ? "Error: " : "Warning: ") + i.Message));
    }

    // ---- confirm + execute (the only write path) --------------------------------------------------

    async void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (_running || _previewedPlan is not { } plan || _previewedSql is not { } sql)
            return;
        if (!_session.IsConnected)
        {
            StatusText.Text = "Not connected. Nothing was executed.";
            return;
        }
        if (_session.HasActiveCommand)
        {
            StatusText.Text = "A command is already running on this connection. Nothing was executed.";
            return;
        }

        var summary = plan.ConfirmationSummary +
                      "\n\nThe server runs this statement on its own; this window shows its result when the statement ends. " +
                      "If the connection is lost the outcome will be reported as unknown, not as stopped.";
        var confirmed = await Dialogs.ConfirmAsync(
            this, plan.IsDestructive ? "Confirm destructive operation" : "Confirm operation", summary, sql,
            plan.IsDestructive ? "Overwrite and run" : "Run", plan.IsDestructive);
        if (!confirmed)
        {
            StatusText.Text = "Cancelled before running. Nothing was executed.";
            return;
        }

        SetRunning(true);
        _cts = new CancellationTokenSource();
        StatusText.Text = "Running on the server… Progress messages are returned when the statement ends. " +
                          "To watch it meanwhile, use Copy progress query and run it from another query tab.";
        try
        {
            var result = await OperationRunner.ExecuteAsync(
                plan, sql, confirmed: true,
                (text, token) => _session.ExecuteAsync(text, token),
                _cts.Token);
            if (result.Outcome is OperationOutcome.Completed or OperationOutcome.OutcomeUnknown)
                Applied = true;
            StatusText.Text = result.Message;
            if (result.Query is { Messages.Count: > 0 } q)
                SqlBox.Text = sql + Environment.NewLine + "-- Server messages --" + Environment.NewLine +
                              string.Join(Environment.NewLine, q.Messages.Select(m => m.Text));
            _previewedPlan = null;
            _previewedSql = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or ArgumentException)
        {
            StatusText.Text = "The operation did not run: " + Safe(ex.Message) + " Nothing was executed.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    void OnCancelRun(object? sender, RoutedEventArgs e)
    {
        _session.RequestCancel();
        _cts?.Cancel();
        StatusText.Text = "Cancel requested. This does not prove the server stopped; the result will say what is known.";
    }

    async void OnCopyProgressQuery(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            StatusText.Text = "Clipboard unavailable.";
            return;
        }
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, OperationRunner.ProgressProbeSql);
        StatusText.Text = "Progress query copied. It is read-only; run it from a query tab.";
    }

    void SetRunning(bool running)
    {
        _running = running;
        RunProgress.IsVisible = running;
        CancelRunButton.IsVisible = running;
        PreviewButton.IsEnabled = !running;
        ConfirmButton.IsEnabled = !running && _previewedPlan is not null;
        CloseButton.IsEnabled = !running;
        Tabs.IsEnabled = !running;
    }

    void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_running)
            return;
        e.Cancel = true;
        StatusText.Text = "An operation is running. Cancel it first; closing this window would not stop the server.";
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();

    static string Safe(string message)
    {
        var oneLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }
}
