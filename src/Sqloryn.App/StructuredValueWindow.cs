using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sqloryn;

/// <summary>Read-only pretty view of a retained cell. It does not re-query and does not execute the text.</summary>
sealed class StructuredValueWindow : Window
{
    public StructuredValueWindow(string column, string text)
    {
        Title = "XML / JSON";
        Width = 720;
        Height = 520;
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        var note = new TextBlock
        {
            Text = "Value from the retained cell \"" + column + "\". Pretty-printed here only. The content was not executed and the server was not queried again.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 10)
        };

        var box = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Menlo, Monaco, Consolas, monospace"),
            FontSize = 13,
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        AutomationProperties.SetName(box, "XML or JSON value");

        var close = new Button { Content = "Close", MinWidth = 88, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Margin = new Avalonia.Thickness(0, 12, 0, 0);
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        DockPanel.SetDock(note, Dock.Top);

        var panel = new DockPanel { Margin = new Avalonia.Thickness(16) };
        panel.Children.Add(close);
        panel.Children.Add(note);
        panel.Children.Add(box);
        Content = panel;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }
}
