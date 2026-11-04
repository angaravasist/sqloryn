using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sqloryn.Core;

namespace Sqloryn;

public partial class MainWindow
{
    DispatcherTimer? _runClock;

    async void OnRunCurrentBatch(object? sender, RoutedEventArgs e)
    {
        if (_active is null)
            return;
        var tab = _active;
        tab.Document.SetText(Editor.Text ?? "");
        CaptureSelection();
        var text = tab.Document.Text;
        var caret = GoBatchExecution.ResolveCaret(
            Editor.CaretOffset, tab.SelectionStart, string.IsNullOrEmpty(tab.Selection) ? 0 : tab.Selection.Length);
        GoBatchExecution.OneBatchPlan plan;
        try
        {
            plan = GoBatchExecution.Plan(text, caret);
        }
        catch (Exception ex) when (ex is SqlScriptRejectedException or InvalidOperationException)
        {
            StatusBar.Text = SafeUi(ex.Message);
            ShowClientMessage(SafeUi(ex.Message));
            return;
        }
        var origin = ScriptExecutionOrigin.FromDocument(text, plan.Start);
        var ran = await ExecuteOnTabAsync(tab, plan.Sql, origin);
        if (ran && ReferenceEquals(_active, tab))
        {
            StatusBar.Text = plan.Note;
            MessagesBox.Text = plan.Note + "\n" + MessagesBox.Text;
            ShowMessagesTab();
        }
    }

    async void OnSessionAnsi(object? sender, RoutedEventArgs e)
    {
        if (_active is null)
            return;
        var tab = _active;
        var dialog = new QueryOptionsWindow();
        var ok = await dialog.ShowDialog<bool>(this);
        if (!ok || !ReferenceEquals(_active, tab))
            return;
        await ExecuteOnTabAsync(tab, dialog.Settings.ToSessionScript(), null);
    }

    /// <summary>Runs SQL on <paramref name="tab"/> only. Returns false when a guard refused before any command was sent.</summary>
    async Task<bool> ExecuteOnTabAsync(QueryTab tab, string sql, ScriptExecutionOrigin? origin)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            StatusBar.Text = "Nothing to run.";
            return false;
        }
        if (!tab.Session.IsConnected)
        {
            StatusBar.Text = "Connect before running SQL.";
            return false;
        }
        if (tab.Session.TargetUnknown)
        {
            StatusBar.Text =
                "Database target unknown after a prior batch. Reconnect or revalidate before running. SQL is never replayed.";
            return false;
        }
        if (tab.IsExecuting || tab.Session.HasActiveCommand || !tab.TryBeginRun())
        {
            StatusBar.Text = "A command is already running on this tab. Use Cancel; Run will not queue or replay SQL.";
            return false;
        }

        tab.RunCts = new CancellationTokenSource();
        var cts = tab.RunCts;
        var binding = tab.CaptureBinding();
        tab.RunStartedUtc = DateTimeOffset.UtcNow;
        ShowRunning(tab, 0, initial: true);
        EnsureRunClock();
        try
        {
            var result = await tab.Session.ExecuteAsync(sql, origin, cts.Token);
            StopRunClock(tab);
            if (!OwnerStillCurrent(tab, binding))
                return true;
            tab.LastResult = result;
            RememberBatch(ExecutionHistory.FromResult(result, DateTimeOffset.UtcNow));
            tab.Status = QueryOutcomeFormatter.DescribeStatus(result);
            var affected = QueryOutcomeFormatter.DescribeRowsAffected(result.RowsAffected);
            var elapsed = QueryOutcomeFormatter.DescribeClientElapsed(result.ClientElapsedMilliseconds);
            if (affected.Length > 0)
                tab.Status += " · " + affected;
            if (elapsed.Length > 0)
                tab.Status += " · " + elapsed;
            if (ReferenceEquals(_active, tab))
            {
                RenderResult(result);
                UpdateStatus();
            }
        }
        catch (Exception ex)
        {
            StopRunClock(tab);
            if (!OwnerStillCurrent(tab, binding))
                return true;
            tab.Status = "Run failed";
            RememberBatch(ExecutionHistory.FromBatch(sql, QueryCompletionKind.Failed, null, DateTimeOffset.UtcNow));
            if (ReferenceEquals(_active, tab))
            {
                _lastError = ex.Message;
                MessagesBox.Text = SafeUi(ex.Message);
                SetResultsVisible(true);
                ShowMessagesTab();
                UpdateStatus();
                StatusBar.Text = SafeUi(ex.Message);
            }
            else
            {
                _lastError = ex.Message;
            }
        }
        finally
        {
            StopRunClock(tab);
            tab.EndRun();
            if (ReferenceEquals(tab.RunCts, cts))
                tab.RunCts = null;
            if (ReferenceEquals(_active, tab))
                UpdateStatus();
        }
        return true;
    }

    void EnsureRunClock()
    {
        if (_runClock is null)
        {
            _runClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _runClock.Tick += (_, _) =>
            {
                if (_active is not { } tab || tab.RunStartedUtc is not DateTimeOffset started || !tab.IsExecuting)
                {
                    if (!_tabs.Any(t => t.IsExecuting))
                        _runClock?.Stop();
                    return;
                }
                var ms = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                ShowRunning(tab, ms < 0 ? 0 : ms, initial: false);
            };
        }
        if (!_runClock.IsEnabled)
            _runClock.Start();
    }

    void StopRunClock(QueryTab tab)
    {
        tab.RunStartedUtc = null;
        if (!_tabs.Any(t => t.RunStartedUtc is not null))
            _runClock?.Stop();
    }

    /// <summary>
    /// Messages and the status bar say Running, with client elapsed time, until the command ends.
    /// A previous success is not left on screen during Execute.
    /// </summary>
    void ShowRunning(QueryTab tab, long elapsedMilliseconds, bool initial)
    {
        if (elapsedMilliseconds < 0)
            elapsedMilliseconds = 0;
        tab.Status = LongRunningStatus.Describe(elapsedMilliseconds);
        if (!ReferenceEquals(_active, tab))
            return;
        UpdateStatus();
        var messages = LongRunningStatus.FormatMessages(elapsedMilliseconds);
        var hasMessages = ResultTabs.Items.OfType<TabItem>().Any(t => t.Tag is -1);
        if (initial || !hasMessages)
        {
            _renderingResults = true;
            try
            {
                ResultTabs.Items.Clear();
                MessagesBox.Text = messages;
                var messagesTab = new TabItem { Header = "Messages", Tag = -1, Content = MessagesBox };
                ResultTabs.Items.Add(messagesTab);
                ResultTabs.SelectedItem = messagesTab;
            }
            finally
            {
                _renderingResults = false;
            }
            SetResultsVisible(true);
        }
        else
        {
            MessagesBox.Text = messages;
        }
        ResultSummary.Text = tab.Status;
    }

    void RememberBatch(ExecutionHistoryEntry entry)
    {
        try
        {
            AppServices.History.Append(entry);
        }
        catch (Exception)
        {
            // A history write must not change the query outcome the user just saw.
        }
    }

    async void OnRefreshSubtree(object? sender, RoutedEventArgs e)
    {
        if (_active is null || SelectedNode is not { } node)
        {
            StatusBar.Text = "Select a database or folder in Object Explorer to refresh that subtree.";
            return;
        }
        if (node.IsLeaf)
        {
            StatusBar.Text = "This item has no children. Select a database or folder to refresh that subtree. The rest of the tree was not reloaded.";
            return;
        }
        var tab = _active;
        _explorer.InvalidateSubtree(node);
        node.IsExpanded = true;
        await ExpandNodeAsync(node);
        if (ReferenceEquals(_active, tab) && ReferenceEquals(SelectedNode, node) && node.Loaded)
            StatusBar.Text = "Refreshed " + node.Title + ". Other object explorer branches were left as they were.";
    }

    void OnChangeDatabase(object? sender, RoutedEventArgs e)
    {
        if (_active?.Session.IsConnected != true)
        {
            StatusBar.Text = "Connect before changing the database. A new login is not requested.";
            return;
        }
        DatabaseBox.Focus();
        DatabaseBox.IsDropDownOpen = true;
    }

    async void OnDatabaseChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressDatabaseChoice || _active is null)
            return;
        if (DatabaseBox.SelectedItem is not string name)
            return;
        var tab = _active;
        if (string.Equals(name, tab.Session.Database, StringComparison.OrdinalIgnoreCase))
            return;
        if (tab.IsExecuting || tab.Session.HasActiveCommand)
        {
            StatusBar.Text = "A command is already running on this tab. The database was not changed.";
            ReselectDatabase(tab.Session.Database);
            return;
        }
        var others = _tabs.Where(t => !ReferenceEquals(t, tab)).Select(t => (IDatabaseSession)t.Session).ToList();
        var previous = tab.Session.Database;
        DatabaseBox.IsEnabled = false;
        try
        {
            var outcome = await ActiveDatabaseSwitch.ApplyAsync(tab.Session, others, name);
            if (!ReferenceEquals(_active, tab))
                return;
            ShowClientMessage(outcome.Message);
            StatusBar.Text = outcome.Message;
            if (!outcome.Applied)
                ReselectDatabase(previous);
            else
                UpdateStatus();
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_active, tab))
                return;
            ReportError(ex.Message);
            ShowClientMessage(SafeUi(ex.Message));
            ReselectDatabase(tab.Session.Database ?? previous);
        }
        finally
        {
            if (ReferenceEquals(_active, tab))
                UpdateCommandState(tab);
        }
    }

    void SetDatabaseChoices(QueryTab tab, IReadOnlyList<string> names)
    {
        if (!ReferenceEquals(_active, tab))
            return;
        var list = names.ToList();
        if (tab.Session.Database is { } current &&
            !list.Exists(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase)))
            list.Insert(0, current);
        _suppressDatabaseChoice = true;
        try
        {
            DatabaseBox.ItemsSource = list;
            DatabaseBox.SelectedItem = list.FirstOrDefault(n =>
                string.Equals(n, tab.Session.Database, StringComparison.OrdinalIgnoreCase));
            DatabaseBox.PlaceholderText = "Database";
        }
        finally
        {
            _suppressDatabaseChoice = false;
        }
    }

    void ReselectDatabase(string? name)
    {
        _suppressDatabaseChoice = true;
        try
        {
            if (DatabaseBox.ItemsSource is System.Collections.IEnumerable items)
            {
                string? match = null;
                foreach (var item in items)
                {
                    if (item is string text && string.Equals(text, name, StringComparison.OrdinalIgnoreCase))
                    {
                        match = text;
                        break;
                    }
                }
                DatabaseBox.SelectedItem = match;
            }
        }
        finally
        {
            _suppressDatabaseChoice = false;
        }
    }

    void ShowClientMessage(string message)
    {
        MessagesBox.Text = message;
        if (!ResultTabs.Items.OfType<TabItem>().Any(t => t.Tag is -1))
            ResultTabs.Items.Add(new TabItem { Header = "Messages", Tag = -1, Content = MessagesBox });
        SetResultsVisible(true);
        ShowMessagesTab();
    }
}
