using Avalonia.Controls;
using Avalonia.Interactivity;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>Modal go-to-line prompt. Validation is <see cref="GoToLine"/>; the result is null when cancelled.</summary>
public partial class GoToLineWindow : Window
{
    readonly int _lineCount;

    public GoToLineWindow() : this(1, 1)
    {
    }

    public GoToLineWindow(int lineCount, int currentLine)
    {
        _lineCount = Math.Max(1, lineCount);
        InitializeComponent();
        PromptText.Text = $"Line number (1–{_lineCount}). Current line: {currentLine}.";
        LineBox.Text = currentLine.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Opened += (_, _) =>
        {
            LineBox.Focus();
            LineBox.SelectAll();
        };
    }

    public GoToTarget? Result { get; private set; }

    void OnGo(object? sender, RoutedEventArgs e)
    {
        if (!GoToLine.TryParse(LineBox.Text, _lineCount, out var target))
        {
            ErrorText.Text = GoToLine.Describe(LineBox.Text, _lineCount);
            ErrorText.IsVisible = true;
            return;
        }
        Result = target;
        Close(true);
    }

    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
