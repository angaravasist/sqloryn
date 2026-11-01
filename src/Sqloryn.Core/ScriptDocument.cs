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

    /// <summary>Snapshot for deferred Open/Quit commit or rollback.</summary>
    public DocumentSnapshot CaptureSnapshot()
        => new(FilePath, Text, IsDirty, _version);

    public void RestoreSnapshot(DocumentSnapshot snapshot)
    {
        FilePath = snapshot.FilePath;
        Text = snapshot.Text;
        IsDirty = snapshot.IsDirty;
        _version = snapshot.Version + 1;
    }

    public async Task LoadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        LoadFrom(path, text);
    }

    /// <summary>
    /// Reads replacement file text without mutating this document.
    /// Caller commits via <see cref="LoadFrom"/> only after Open succeeds.
    /// </summary>
    public static async Task<string> ReadFileTextAsync(string path, CancellationToken cancellationToken = default)
        => await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

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
            throw;
        }

        CommitSave(target, snapshot, version);
    }

    public void CommitSave(string path, string snapshot, long versionAtCapture)
    {
        FilePath = path;
        if (_version == versionAtCapture && Text == snapshot)
            IsDirty = false;
    }

    public static string ResolveBatch(string document, string? selection)
        => !string.IsNullOrEmpty(selection) ? selection : document;
}

public readonly record struct DocumentSnapshot(string? FilePath, string Text, bool IsDirty, long Version);

/// <summary>Deferred dirty resolution: destructive changes apply only on commit.</summary>
public static class DocumentLifecycle
{
    public enum PromptChoice
    {
        Save,
        Discard,
        Cancel
    }

    public enum PendingActionKind
    {
        None,
        Discard,
        Saved
    }

    public sealed class PendingAction
    {
        public required ScriptDocument Document { get; init; }
        public required DocumentSnapshot Before { get; init; }
        public required PendingActionKind Kind { get; init; }
    }

    /// <summary>
    /// Prompts for a dirty document without applying Discard. Save may run immediately
    /// (persistent side effect); Discard is recorded as pending until <see cref="Commit"/>.
    /// </summary>
    public static async Task<(bool Proceed, PendingAction? Pending)> ResolveDirtyDeferredAsync(
        ScriptDocument document,
        Func<Task<PromptChoice>> prompt,
        Func<ScriptDocument, Task<bool>> save)
    {
        if (!document.IsDirty)
            return (true, null);

        var before = document.CaptureSnapshot();
        var choice = await prompt().ConfigureAwait(true);
        switch (choice)
        {
            case PromptChoice.Cancel:
                return (false, null);
            case PromptChoice.Discard:
                return (true, new PendingAction
                {
                    Document = document,
                    Before = before,
                    Kind = PendingActionKind.Discard
                });
            case PromptChoice.Save:
                if (!await save(document).ConfigureAwait(true))
                    return (false, null);
                return (true, new PendingAction
                {
                    Document = document,
                    Before = before,
                    Kind = PendingActionKind.Saved
                });
            default:
                return (false, null);
        }
    }

    public static void Commit(IEnumerable<PendingAction> actions)
    {
        foreach (var action in actions)
        {
            if (action.Kind == PendingActionKind.Discard)
                action.Document.DiscardChangesTo(action.Before.Text);
        }
    }

    /// <summary>
    /// Cancels pending intents. Deferred Discard never mutated the document, so no snapshot
    /// restoration is performed (avoids clobbering newer edits).
    /// </summary>
    public static void Rollback(IEnumerable<PendingAction> actions)
    {
        // Discard was deferred and never applied — nothing to restore.
        // Saved actions already persisted; leave buffer as-is.
        _ = actions;
    }

    /// <summary>
    /// Applies a successful Open replacement only when the document version is unchanged
    /// since <paramref name="versionAtReadStart"/>. Newer edits win; replacement is abandoned.
    /// </summary>
    public static bool TryCommitOpenReplacement(
        ScriptDocument document,
        string path,
        string fileText,
        long versionAtReadStart)
    {
        if (document.Version != versionAtReadStart)
            return false;
        document.LoadFrom(path, fileText);
        return true;
    }

    /// <summary>
    /// Quit/close-all: collect intents; on cancel at any prompt, roll back prior pending discards
    /// so every still-open buffer keeps its unsaved text and dirty flag.
    /// </summary>
    public static async Task<(bool Proceed, List<PendingAction> Pending)> ResolveAllDirtyDeferredAsync(
        IReadOnlyList<ScriptDocument> documents,
        Func<ScriptDocument, int, Task<PromptChoice>> prompt,
        Func<ScriptDocument, Task<bool>> save)
    {
        var pending = new List<PendingAction>();
        for (var i = 0; i < documents.Count; i++)
        {
            var doc = documents[i];
            var (ok, action) = await ResolveDirtyDeferredAsync(
                doc,
                () => prompt(doc, i),
                save).ConfigureAwait(true);
            if (!ok)
            {
                Rollback(pending);
                return (false, pending);
            }
            if (action is not null)
                pending.Add(action);
        }
        return (true, pending);
    }

    /// <summary>Open flow: discard stays pending until a replacement file is successfully loaded.</summary>
    public static async Task<(bool Proceed, PendingAction? Pending)> BeginOpenAsync(
        ScriptDocument document,
        Func<Task<PromptChoice>> prompt,
        Func<ScriptDocument, Task<bool>> save)
        => await ResolveDirtyDeferredAsync(document, prompt, save).ConfigureAwait(true);
}
