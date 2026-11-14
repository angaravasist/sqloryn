using Avalonia.Controls;
using Avalonia.Interactivity;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>
/// Session ANSI settings for the active tab. Preview is the exact script Apply will run.
/// Connect timeout and command timeout are not part of this dialog.
/// </summary>
public partial class QueryOptionsWindow : Window
{
    public QueryOptionsWindow()
    {
        InitializeComponent();
        ScopeText.Text = QueryAnsiSettings.ScopeNote;
        RefreshPreview();
    }

    public QueryAnsiSettings Settings => new(
        AnsiNullsBox.IsChecked == true,
        AnsiPaddingBox.IsChecked == true,
        QuotedIdentifierBox.IsChecked == true,
        ArithAbortBox.IsChecked == true);

    void OnOptionChanged(object? sender, RoutedEventArgs e) => RefreshPreview();

    void RefreshPreview() => PreviewBox.Text = Settings.ToSessionScript();

    void OnApply(object? sender, RoutedEventArgs e) => Close(true);

    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
