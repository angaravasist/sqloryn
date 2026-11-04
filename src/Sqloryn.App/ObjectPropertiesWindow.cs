using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>Read-only properties sheet. It does not change the server.</summary>
sealed class ObjectPropertiesWindow : Window
{
    public ObjectPropertiesWindow(string objectTitle, string text)
    {
        Title = "Properties";
        Width = 560;
        Height = 440;
        MinWidth = 420;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        var note = new TextBlock
        {
            Text = "Read-only properties for " + SchemaObjectCatalog.OneLine(objectTitle) + ". Nothing is changed on the server.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 10)
        };
        AutomationProperties.SetName(note, "Object properties note");

        var box = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Menlo, Monaco, Consolas, monospace"),
            FontSize = 13,
            Text = text,
            MinHeight = 220
        };
        AutomationProperties.SetName(box, "Object properties sheet");

        var close = new Button { Content = "Close", MinWidth = 88, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        close.Margin = new Avalonia.Thickness(0, 12, 0, 0);
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
