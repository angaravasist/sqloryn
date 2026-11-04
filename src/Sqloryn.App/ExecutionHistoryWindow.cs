using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>Lists recent batches and returns one to open in the editor. It does not run SQL.</summary>
sealed class ExecutionHistoryWindow : Window
{
    ExecutionHistoryWindow(IReadOnlyList<ExecutionHistoryEntry> entries, TaskCompletionSource<ExecutionHistoryEntry?> chosen)
    {
        Title = "Execution history";
        Width = 720;
        Height = 480;
        MinWidth = 520;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        var rows = entries.Select(entry => new HistoryRow(entry, Describe(entry))).ToList();
        var list = new ListBox
        {
            ItemsSource = rows,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        AutomationProperties.SetName(list, "Recent batches");

        var open = new Button { Content = "Open in Editor", MinWidth = 140, IsEnabled = false };
        open.Classes.Add("primary");
        var close = new Button { Content = "Close", MinWidth = 88 };
        open.Click += (_, _) =>
        {
            if (list.SelectedItem is HistoryRow row)
            {
                chosen.TrySetResult(row.Entry);
                Close();
            }
        };
        close.Click += (_, _) => Close();
        list.SelectionChanged += (_, _) => open.IsEnabled = list.SelectedItem is HistoryRow;
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is HistoryRow row)
            {
                chosen.TrySetResult(row.Entry);
                Close();
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(close);
        buttons.Children.Add(open);
        DockPanel.SetDock(buttons, Dock.Bottom);

        var note = new TextBlock
        {
            Text = "Recent batches stored on this Mac: SQL text, outcome and client elapsed time. Result rows are not stored and nothing is sent anywhere. Open in Editor does not run the SQL.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(note, Dock.Top);

        var panel = new DockPanel { Margin = new Avalonia.Thickness(16) };
        panel.Children.Add(buttons);
        panel.Children.Add(note);
        if (rows.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No batches have been recorded on this Mac yet.",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        else
        {
            panel.Children.Add(list);
        }

        Content = panel;
        Closed += (_, _) => chosen.TrySetResult(null);
    }

    public static async Task<ExecutionHistoryEntry?> PickAsync(Window owner, IReadOnlyList<ExecutionHistoryEntry> entries)
    {
        var chosen = new TaskCompletionSource<ExecutionHistoryEntry?>();
        var dialog = new ExecutionHistoryWindow(entries, chosen);
        await dialog.ShowDialog(owner);
        return await chosen.Task;
    }

    static string Describe(ExecutionHistoryEntry entry)
    {
        var when = entry.RecordedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        var elapsed = entry.ElapsedMilliseconds is long ms
            ? ms.ToString(CultureInfo.CurrentCulture) + " ms on this computer"
            : "elapsed time was not measured";
        var sql = entry.Sql.Replace('\r', ' ').Replace('\n', ' ');
        if (sql.Length > 140)
            sql = sql[..140] + "…";
        return when + "   " + entry.Outcome + "   " + elapsed + "    " + sql;
    }

    sealed record HistoryRow(ExecutionHistoryEntry Entry, string Label)
    {
        public override string ToString() => Label;
    }
}
