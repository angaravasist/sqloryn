using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sqloryn;

/// <summary>Small code-built modal dialogs shared by the studio windows.</summary>
static class Dialogs
{
    public static async Task ShowMessageAsync(Window owner, string title, string message, double width = 520, double height = 260)
    {
        var dialog = BuildDialog(title, width, height);
        var panel = new DockPanel { Margin = new Avalonia.Thickness(16) };
        var ok = new Button { Content = "OK", MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Classes.Add("primary");
        ok.Click += (_, _) => dialog.Close();
        DockPanel.SetDock(ok, Avalonia.Controls.Dock.Bottom);
        panel.Children.Add(ok);
        panel.Children.Add(new ScrollViewer
        {
            Margin = new Avalonia.Thickness(0, 0, 0, 12),
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
        });
        dialog.Content = panel;
        await dialog.ShowDialog(owner);
    }

    /// <summary>Confirmation with scrollable detail (for example DDL or SQL preview). Returns true only on explicit confirm.</summary>
    public static async Task<bool> ConfirmAsync(
        Window owner,
        string title,
        string summary,
        string? detail,
        string confirmLabel,
        bool destructive = false,
        double width = 640,
        double height = 460)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = BuildDialog(title, width, height);
        var panel = new DockPanel { Margin = new Avalonia.Thickness(16) };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 12, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        var confirm = new Button { Content = confirmLabel, MinWidth = 110 };
        confirm.Classes.Add(destructive ? "danger" : "primary");
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        confirm.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Bottom);
        panel.Children.Add(buttons);

        var summaryBlock = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(summaryBlock, Avalonia.Controls.Dock.Top);
        panel.Children.Add(summaryBlock);

        if (detail is not null)
        {
            var box = new TextBox
            {
                Text = detail,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap
            };
            box.Classes.Add("mono");
            panel.Children.Add(box);
        }

        dialog.Content = panel;
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    static Window BuildDialog(string title, double width, double height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        CanResize = true,
        ShowInTaskbar = false
    };
}
