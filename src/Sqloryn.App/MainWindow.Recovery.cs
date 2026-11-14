using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Sqloryn.Core;

namespace Sqloryn;

public partial class MainWindow
{
    /// <summary>Queues the open tabs for the debounced recovery writer. No disk I/O on the UI thread.</summary>
    void PersistRecovery(params string[] removedIds)
    {
        if (_suspendRecoveryPersist)
            return;
        _recoveryWriter.Schedule(
            _tabs.Select(t => new RecoveryTabState(
                t.Id.ToString("N"), t.Document.Title, t.Document.FilePath, t.Document.Text, t.Document.IsDirty)),
            removedIds);
    }

    /// <summary>Clears execution history and unsaved copies after explicit confirmation in Settings.</summary>
    async Task ClearPrivateDataAsync()
    {
        try
        {
            AppServices.History.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusBar.Text = "Could not clear execution history: " + SafeUi(ex.Message);
            return;
        }
        await _recoveryWriter.ClearAsync();
        StatusBar.Text = _recoveryWriter.Enabled
            ? "Cleared execution history and unsaved copies. Tabs that are still unsaved are copied again on the next edit."
            : "Cleared execution history and unsaved copies.";
    }

    async void OnRecoverUnsaved(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<RecoveryDocument> found;
        try
        {
            var path = AppServices.Recovery.FilePath;
            var existed = File.Exists(path);
            found = AppServices.Recovery.Read();
            if (found.Count == 0 && existed && File.Exists(path))
            {
                StatusBar.Text = "Unsaved script copies are on this Mac but could not be read. They were not deleted.";
                return;
            }
        }
        catch (Exception ex)
        {
            StatusBar.Text = "Could not read unsaved script copies: " + SafeUi(ex.Message);
            return;
        }

        if (found.Count == 0)
        {
            if (_announceEmptyRecovery)
                StatusBar.Text = "No unsaved script copies are stored on this Mac.";
            return;
        }

        var summary = DocumentRecovery.OfferSummary(found);
        var choice = await ShowRecoveryChoiceAsync(summary, found);
        if (choice == "discard")
        {
            await _recoveryWriter.ClearAsync();
            StatusBar.Text = "Discarded the unsaved recovery copies.";
            return;
        }

        if (choice != "restore")
        {
            StatusBar.Text = "Unsaved recovery copies were kept. File > Recover Unsaved Scripts… can open them later.";
            return;
        }

        _suspendRecoveryPersist = true;
        try
        {
            foreach (var doc in found)
                RestoreRecoveredDocument(doc);
        }
        finally
        {
            _suspendRecoveryPersist = false;
        }

        // The restored tabs own the text now; drop the old copies (all of them when recovery is off).
        if (_recoveryWriter.Enabled)
            PersistRecovery(found.Select(d => d.Id).ToArray());
        else
            await _recoveryWriter.ClearAsync();
        StatusBar.Text = "Restored unsaved scripts into query tabs. They stay unsaved until you save them. The SQL was not run.";
    }

    void RestoreRecoveredDocument(RecoveryDocument doc)
    {
        var blank = _tabs.Count == 1
            && !_tabs[0].Document.IsDirty
            && _tabs[0].Document.Text.Length == 0
            && _tabs[0].Document.FilePath is null;
        var tab = blank ? _tabs[0] : AddTab();
        try
        {
            tab.Document.RestoreDirty(doc.FilePath, doc.Text);
        }
        catch (Exception)
        {
            tab.Document.RestoreDirty(null, doc.Text);
        }

        if (ReferenceEquals(_active, tab))
            Editor.Text = tab.Document.Text;
        RefreshHeader(tab);
        _welcomeDismissed = true;
        UpdateWelcome();
    }

    async Task<string> ShowRecoveryChoiceAsync(string summary, IReadOnlyList<RecoveryDocument> documents)
    {
        var tcs = new TaskCompletionSource<string>();
        var dialog = new Window
        {
            Title = "Recover unsaved SQL",
            Width = 640,
            Height = 460,
            MinWidth = 480,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };
        var panel = new DockPanel { Margin = new Avalonia.Thickness(16) };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 12, 0, 0)
        };
        Button Btn(string label, string value, string? cls = null)
        {
            var button = new Button { Content = label, MinWidth = 120 };
            if (cls is not null)
                button.Classes.Add(cls);
            button.Click += (_, _) =>
            {
                tcs.TrySetResult(value);
                dialog.Close();
            };
            return button;
        }

        row.Children.Add(Btn("Keep for later", "keep"));
        row.Children.Add(Btn("Discard copies", "discard", "danger"));
        row.Children.Add(Btn("Restore", "restore", "primary"));
        DockPanel.SetDock(row, Dock.Bottom);
        panel.Children.Add(row);
        var summaryBlock = new TextBlock
        {
            Text = summary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(summaryBlock, Dock.Top);
        panel.Children.Add(summaryBlock);
        var preview = new StringBuilder();
        foreach (var doc in documents)
        {
            preview.Append(doc.Title);
            if (!string.IsNullOrEmpty(doc.FilePath))
                preview.Append("  ").Append(doc.FilePath);
            preview.Append('\n');
            var snippet = doc.Text.Length <= 400 ? doc.Text : doc.Text[..400] + "…";
            preview.AppendLine(snippet);
            preview.AppendLine();
        }

        var box = new TextBox
        {
            Text = preview.ToString(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap
        };
        box.Classes.Add("mono");
        AutomationProperties.SetName(box, "Unsaved scripts");
        panel.Children.Add(box);
        dialog.Content = panel;
        dialog.Closed += (_, _) => tcs.TrySetResult("keep");
        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    async void OnExecutionHistory(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<ExecutionHistoryEntry> entries;
        try
        {
            entries = AppServices.History.Read();
        }
        catch (Exception ex)
        {
            StatusBar.Text = "Could not read execution history: " + SafeUi(ex.Message);
            return;
        }

        var picked = await ExecutionHistoryWindow.PickAsync(this, entries);
        if (picked is null)
            return;
        OpenRecalledBatch(ExecutionHistory.ScriptForEditor(picked));
    }

    void OpenRecalledBatch(string sql)
    {
        _welcomeDismissed = true;
        var tab = new QueryTab();
        tab.Document.SetText(sql);
        AddTab(tab);
        StatusBar.Text = "Opened a previous batch in a new query tab. It was not run.";
    }
}
