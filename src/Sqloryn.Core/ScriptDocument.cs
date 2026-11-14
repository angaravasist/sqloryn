namespace Sqloryn.Core;

/// <summary>UTF-8 .sql buffer with dirty tracking. Does not execute SQL.</summary>
public sealed class ScriptDocument
{
    long _version;
    TrackedDiskState? _disk;

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
        _disk = null;
        _version++;
    }

    /// <summary>
    /// Replaces the buffer with <paramref name="text"/> read from <paramref name="path"/>.
    /// Pass the <paramref name="fingerprint"/> of the exact bytes that were read (see <see cref="ReadFileAsync"/>);
    /// when omitted, the file's current bytes are fingerprinted now (a file that does not exist is tracked as missing).
    /// </summary>
    public void LoadFrom(string path, string text, FileFingerprint? fingerprint = null)
    {
        FilePath = path;
        Text = text;
        IsDirty = false;
        var resolved = SafeFileWriter.ResolveDestination(path);
        _disk = new TrackedDiskState(resolved, fingerprint ?? FileFingerprint.TryCapture(resolved));
        _version++;
    }

    /// <summary>Puts a recovered script back and leaves it dirty so it is not saved or discarded silently.</summary>
    public void RestoreDirty(string? path, string text)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            FilePath = null;
            _disk = null;
        }
        else
        {
            FilePath = path;
            var resolved = SafeFileWriter.ResolveDestination(path);
            _disk = new TrackedDiskState(resolved, FileFingerprint.TryCapture(resolved));
        }

        Text = text ?? "";
        IsDirty = true;
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
        => new(FilePath, Text, IsDirty, _version, _disk);

    public void RestoreSnapshot(DocumentSnapshot snapshot)
    {
        FilePath = snapshot.FilePath;
        _disk = snapshot.Disk;
        Text = snapshot.Text;
        IsDirty = snapshot.IsDirty;
        _version = snapshot.Version + 1;
    }

    public async Task LoadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var (text, fingerprint) = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        LoadFrom(path, text, fingerprint);
    }

    /// <summary>
    /// Reads file text and the fingerprint of the exact bytes decoded, without mutating any document.
    /// Commit with <see cref="LoadFrom"/> so later saves can detect external edits.
    /// </summary>
    public static async Task<(string Text, FileFingerprint Fingerprint)> ReadFileAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        return (reader.ReadToEnd(), FileFingerprint.FromBytes(bytes));
    }

    /// <summary>
    /// Reads replacement file text without mutating this document.
    /// Caller commits via <see cref="LoadFrom"/> only after Open succeeds.
    /// </summary>
    public static async Task<string> ReadFileTextAsync(string path, CancellationToken cancellationToken = default)
        => (await ReadFileAsync(path, cancellationToken).ConfigureAwait(false)).Text;

    /// <summary>
    /// Stages then replaces the file. When the target is the file this document last loaded/saved and its on-disk
    /// bytes changed since, throws <see cref="ExternalDocumentChangedException"/> before the replace, leaving the
    /// external content and this buffer (still dirty) untouched. <paramref name="forceSave"/> skips that check
    /// (explicit overwrite after user confirmation). The check runs right before the final rename; a write landing
    /// in the remaining instant between check and rename cannot be detected without OS file locking.
    /// </summary>
    public async Task SaveAsync(
        string? path = null,
        CancellationToken cancellationToken = default,
        Func<string, string, CancellationToken, Task>? writer = null,
        Func<Task>? betweenCaptureAndWrite = null,
        bool forceSave = false)
    {
        var target = path ?? FilePath ?? throw new InvalidOperationException("Save path is required.");
        var snapshot = Text;
        var version = _version;
        if (betweenCaptureAndWrite is not null)
            await betweenCaptureAndWrite().ConfigureAwait(false);
        FileFingerprint? written;
        if (writer is null)
        {
            await SafeFileWriter.WriteUtf8Async(
                target,
                snapshot,
                cancellationToken,
                beforeCommit: _ =>
                {
                    VerifyUnchangedOnDisk(target, forceSave);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            written = FileFingerprint.FromBytes(new System.Text.UTF8Encoding(false).GetBytes(snapshot));
        }
        else
        {
            VerifyUnchangedOnDisk(target, forceSave);
            await writer(target, snapshot, cancellationToken).ConfigureAwait(false);
            written = FileFingerprint.TryCapture(SafeFileWriter.ResolveDestination(target));
        }

        CommitSave(target, snapshot, version, written);
    }

    void VerifyUnchangedOnDisk(string target, bool forceSave)
    {
        if (forceSave || _disk is null)
            return;
        var resolved = SafeFileWriter.ResolveDestination(target);
        if (!string.Equals(resolved, _disk.ResolvedPath, StringComparison.Ordinal))
            return; // Save As to a different file: not the one we are tracking.
        if (FileFingerprint.TryCapture(resolved) != _disk.Fingerprint)
            throw new ExternalDocumentChangedException(target);
    }

    public void CommitSave(string path, string snapshot, long versionAtCapture)
        => CommitSave(path, snapshot, versionAtCapture, FileFingerprint.TryCapture(SafeFileWriter.ResolveDestination(path)));

    void CommitSave(string path, string snapshot, long versionAtCapture, FileFingerprint? writtenFingerprint)
    {
        FilePath = path;
        _disk = new TrackedDiskState(SafeFileWriter.ResolveDestination(path), writtenFingerprint);
        if (_version == versionAtCapture && Text == snapshot)
            IsDirty = false;
    }

    public static string ResolveBatch(string document, string? selection)
        => !string.IsNullOrEmpty(selection) ? selection : document;
}

public readonly record struct DocumentSnapshot(
    string? FilePath, string Text, bool IsDirty, long Version, TrackedDiskState? Disk = null);

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
        long versionAtReadStart,
        FileFingerprint? fingerprint = null)
    {
        if (document.Version != versionAtReadStart)
            return false;
        document.LoadFrom(path, fileText, fingerprint);
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
