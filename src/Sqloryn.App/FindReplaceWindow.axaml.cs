using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaloniaEdit;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>
/// Modeless find/replace over the editor text. All matching is delegated to <see cref="FindReplaceEngine"/>.
/// The edited text is written back to the editor and exposed as <see cref="UpdatedText"/>.
/// </summary>
public partial class FindReplaceWindow : Window
{
    readonly TextEditor _editor;

    public FindReplaceWindow() : this(new TextEditor())
    {
    }

    public FindReplaceWindow(TextEditor editor)
    {
        _editor = editor;
        InitializeComponent();
        UpdatedText = editor.Text ?? "";
        var selected = SelectedText();
        if (!string.IsNullOrEmpty(selected) && !selected.Contains('\n'))
            FindBox.Text = selected;
    }

    /// <summary>Editor text after the most recent replace operation (or the original text).</summary>
    public string UpdatedText { get; private set; }

    /// <summary>Raised after Replace or Replace All wrote new text into the editor.</summary>
    public event Action<string>? TextReplaced;

    string EditorText => _editor.Text ?? "";

    (int Start, int End) Selection()
    {
        var text = EditorText;
        var start = Math.Clamp(_editor.SelectionStart, 0, text.Length);
        var end = Math.Clamp(_editor.SelectionStart + _editor.SelectionLength, start, text.Length);
        return (start, end);
    }

    string SelectedText()
    {
        var (start, end) = Selection();
        return EditorText[start..end];
    }

    bool CaseSensitive => CaseBox.IsChecked == true;
    string Query => FindBox.Text ?? "";

    bool SelectionMatches()
    {
        var query = Query;
        if (query.Length == 0)
            return false;
        var selected = SelectedText();
        return selected.Length == query.Length &&
               string.Equals(selected, query, CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    bool FindAndSelect(int from)
    {
        if (Query.Length == 0)
        {
            StatusText.Text = "Enter text to find.";
            return false;
        }
        var index = FindReplaceEngine.FindNext(EditorText, Query, from, CaseSensitive);
        if (index < 0)
        {
            StatusText.Text = "No matches.";
            return false;
        }
        _editor.Select(index, Query.Length);
        _editor.TextArea.Caret.BringCaretToView();
        StatusText.Text = $"Match at offset {index}.";
        return true;
    }

    void OnFindNext(object? sender, RoutedEventArgs e)
    {
        var (_, end) = Selection();
        FindAndSelect(end);
    }

    void OnReplace(object? sender, RoutedEventArgs e)
    {
        if (Query.Length == 0)
        {
            StatusText.Text = "Enter text to find.";
            return;
        }
        if (!SelectionMatches())
        {
            // Mirror common editors: first press selects the next match.
            FindAndSelect(Selection().End);
            return;
        }
        var (start, end) = Selection();
        var text = EditorText;
        var replacement = ReplaceBox.Text ?? "";
        var updated = text[..start] + replacement + text[end..];
        Apply(updated);
        _editor.Select(start, replacement.Length);
        FindAndSelect(start + replacement.Length);
    }

    void OnReplaceAll(object? sender, RoutedEventArgs e)
    {
        if (Query.Length == 0)
        {
            StatusText.Text = "Enter text to find.";
            return;
        }
        var result = FindReplaceEngine.ReplaceAll(EditorText, Query, ReplaceBox.Text ?? "", CaseSensitive);
        if (result.Count == 0)
        {
            StatusText.Text = "No matches.";
            return;
        }
        Apply(result.Text);
        StatusText.Text = $"Replaced {result.Count} occurrence(s).";
    }

    void Apply(string text)
    {
        _editor.Text = text;
        UpdatedText = text;
        TextReplaced?.Invoke(text);
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();
}
