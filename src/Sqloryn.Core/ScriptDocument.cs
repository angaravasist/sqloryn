namespace Sqloryn.Core;

/// <summary>UTF-8 .sql buffer with dirty tracking. Does not execute SQL.</summary>
public sealed class ScriptDocument
{
    long _version;

    public string? FilePath { get; private set; }
    public string Text { get; private set; } = "";
    public bool IsDirty { get; private set; }
    public long Version => _version;
    public string Title => string.IsNullOrEmpty(FilePath) ? "Untitled.sql" : Path.GetFileName(FilePath);

    public void New()
    {
        FilePath = null;
        Text = "";
        IsDirty = false;
        _version++;
    }

    public void LoadFrom(string path, string text)
    {
        FilePath = path;
        Text = text;
        IsDirty = false;
        _version++;
    }

    public void SetText(string text)
    {
        if (Text == text)
            return;
        Text = text;
        IsDirty = true;
        _version++;
    }

    public void DiscardChangesTo(string text)
    {
        Text = text;
        IsDirty = false;
        _version++;
    }

    public async Task LoadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        LoadFrom(path, text);
    }

    /// <summary>
    /// Writes the snapshot captured at start. Clears dirty only if no newer edits arrived.
    /// On I/O failure, leaves dirty state unchanged and rethrows.
    /// </summary>
    public async Task SaveAsync(
        string? path = null,
        CancellationToken cancellationToken = default,
        Func<string, string, CancellationToken, Task>? writer = null,
        Func<Task>? betweenCaptureAndWrite = null)
    {
        var target = path ?? FilePath ?? throw new InvalidOperationException("Save path is required.");
        var snapshot = Text;
        var version = _version;
        if (betweenCaptureAndWrite is not null)
            await betweenCaptureAndWrite().ConfigureAwait(false);
        try
        {
            if (writer is null)
                await File.WriteAllTextAsync(target, snapshot, cancellationToken).ConfigureAwait(false);
            else
                await writer(target, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Preserve dirty / path for recovery; never claim a successful save.
            throw;
        }

        CommitSave(target, snapshot, version);
    }

    /// <summary>Applies a completed write. Stays dirty when newer edits arrived during the save.</summary>
    public void CommitSave(string path, string snapshot, long versionAtCapture)
    {
        FilePath = path;
        if (_version == versionAtCapture && Text == snapshot)
            IsDirty = false;
        // else: keep IsDirty true — newer edits or concurrent SetText.
    }

    /// <summary>Preview rule: selection when non-empty; otherwise the whole document.</summary>
    public static string ResolveBatch(string document, string? selection)
        => !string.IsNullOrEmpty(selection) ? selection : document;
}

/// <summary>Pure document/prompt sequencing for Open/Quit/Close-tab without UI frameworks.</summary>
public static class DocumentLifecycle
{
    public enum PromptChoice
    {
        Save,
        Discard,
        Cancel
    }

    /// <summary>
    /// Returns whether the caller may proceed (open/close/quit). Cancel leaves the document dirty.
    /// </summary>
    public static async Task<bool> ResolveDirtyAsync(
        ScriptDocument document,
        Func<Task<PromptChoice>> prompt,
        Func<ScriptDocument, Task<bool>> save,
        Func<ScriptDocument, Task>? discard = null)
    {
        if (!document.IsDirty)
            return true;
        var choice = await prompt().ConfigureAwait(false);
        switch (choice)
        {
            case PromptChoice.Cancel:
                return false;
            case PromptChoice.Discard:
                if (discard is not null)
                    await discard(document).ConfigureAwait(false);
                else
                    document.DiscardChangesTo(document.Text);
                return true;
            case PromptChoice.Save:
                return await save(document).ConfigureAwait(false);
            default:
                return false;
        }
    }

    /// <summary>
    /// Walks dirty tabs in order. Cancel at any prompt aborts and leaves remaining tabs untouched
    /// (already-resolved tabs keep the user's Save/Discard choice).
    /// </summary>
    public static async Task<bool> ResolveAllDirtyAsync(
        IReadOnlyList<ScriptDocument> documents,
        Func<ScriptDocument, int, Task<PromptChoice>> prompt,
        Func<ScriptDocument, Task<bool>> save)
    {
        for (var i = 0; i < documents.Count; i++)
        {
            var doc = documents[i];
            var ok = await ResolveDirtyAsync(
                doc,
                () => prompt(doc, i),
                save).ConfigureAwait(false);
            if (!ok)
                return false;
        }
        return true;
    }
}
