namespace Sqloryn.Core;

/// <summary>UTF-8 .sql buffer with dirty tracking. Does not execute SQL.</summary>
public sealed class ScriptDocument
{
    public string? FilePath { get; private set; }
    public string Text { get; private set; } = "";
    public bool IsDirty { get; private set; }
    public string Title => string.IsNullOrEmpty(FilePath) ? "Untitled.sql" : Path.GetFileName(FilePath);

    public void New()
    {
        FilePath = null;
        Text = "";
        IsDirty = false;
    }

    public void LoadFrom(string path, string text)
    {
        FilePath = path;
        Text = text;
        IsDirty = false;
    }

    public void SetText(string text)
    {
        if (Text == text)
            return;
        Text = text;
        IsDirty = true;
    }

    public void DiscardChangesTo(string text)
    {
        Text = text;
        IsDirty = false;
    }

    public async Task LoadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        LoadFrom(path, text);
    }

    public async Task SaveAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        var target = path ?? FilePath ?? throw new InvalidOperationException("Save path is required.");
        await File.WriteAllTextAsync(target, Text, cancellationToken).ConfigureAwait(false);
        FilePath = target;
        IsDirty = false;
    }

    /// <summary>Preview rule: selection when non-empty; otherwise the whole document.</summary>
    public static string ResolveBatch(string document, string? selection)
        => !string.IsNullOrEmpty(selection) ? selection : document;
}
