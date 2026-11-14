using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>
/// Honest gated shell for capabilities that are not implemented or not verified (DELIVERY-CONTRACT §10-11). It renders a
/// <see cref="GatedWorkspace"/> model only: every section names its missing fixture, protocol or authentication, the
/// section action is bound to the model and stays disabled, and the window reads nothing from a server, collects no
/// credentials and makes no network or cloud call.
/// </summary>
public class GatedWorkspaceWindow : Window
{
    public GatedWorkspaceWindow(GatedWorkspace workspace)
    {
        Title = workspace.Title + " — SQLoryn";
        Width = 760;
        Height = 600;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 88 };
        close.Click += (_, _) => Close();

        var header = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = workspace.Title, FontSize = 18, FontWeight = FontWeight.SemiBold });
        var status = new TextBlock { Text = workspace.StatusLine, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(status, workspace.Title + " status");
        header.Children.Add(status);
        var summary = new TextBlock { Text = workspace.Summary, TextWrapping = TextWrapping.Wrap };
        summary.Classes.Add("muted");
        header.Children.Add(summary);

        var list = new StackPanel { Spacing = 10 };
        foreach (var section in workspace.Sections)
            list.Children.Add(BuildSection(section));

        var panel = new DockPanel { Margin = new Avalonia.Thickness(16) };
        DockPanel.SetDock(close, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        panel.Children.Add(close);
        panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer { Margin = new Avalonia.Thickness(0, 0, 0, 10), Content = list });
        Content = panel;
    }

    static Border BuildSection(GateSection section)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = section.Title, FontWeight = FontWeight.SemiBold });
        var state = new TextBlock { Text = section.StatusText, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(state, section.Title + " status");
        stack.Children.Add(state);
        var explanation = new TextBlock { Text = section.Explanation, TextWrapping = TextWrapping.Wrap };
        explanation.Classes.Add("muted");
        stack.Children.Add(explanation);
        stack.Children.Add(new TextBlock { Text = "Missing: " + section.MissingRequirement, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock
        {
            Text = "Tracked as " + string.Join(", ", section.CapIds) + " in docs/ssms/CAP_EVIDENCE.csv",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        var action = new Button
        {
            Content = section.ActionLabel,
            IsEnabled = section.ActionEnabled,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (!section.ActionEnabled)
            action.IsEnabled = false;
        AutomationProperties.SetName(action, section.Title + ": " + section.ActionLabel);
        stack.Children.Add(action);
        return new Border
        {
            BorderThickness = new Avalonia.Thickness(1),
            BorderBrush = Brushes.Gray,
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(12),
            Child = stack
        };
    }
}

public sealed class HighAvailabilityWorkspace() : GatedWorkspaceWindow(GatedWorkspaceCatalog.HighAvailability);

public sealed class ReplicationWorkspace() : GatedWorkspaceWindow(GatedWorkspaceCatalog.Replication);

public sealed class BiCloudWorkspace() : GatedWorkspaceWindow(GatedWorkspaceCatalog.BiCloud);
