using System.Text.Json;

namespace Sqloryn.Core;

/// <summary>
/// One dirty query tab copied under Application Support. SQL text only: no server, login or connection string.
/// Credential-like assignments in the text are redacted before the copy is written.
/// </summary>
public sealed record RecoveryDocument(
    string Id,
    string Title,
    string? FilePath,
    string Text,
    bool TextWasRedacted,
    bool TextWasTruncated,
    DateTimeOffset CapturedUtc);

/// <summary>Selects which unsaved copies to keep. Never drops a copy unless that tab was saved, restored or explicitly removed by the caller.</summary>
public static class DocumentRecovery
{
    public const int MaxDocuments = 20;
    public const int MaxTextChars = 200_000;

    public static RecoveryDocument Capture(string id, string title, string? filePath, string text, DateTimeOffset capturedUtc)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A document id is required.", nameof(id));
        var original = text ?? "";
        var redacted = DiagnosticsRedactor.Redact(original);
        var wasRedacted = !string.Equals(redacted, original, StringComparison.Ordinal);
        var truncated = false;
        if (redacted.Length > MaxTextChars)
        {
            redacted = redacted[..MaxTextChars];
            truncated = true;
        }

        var safeTitle = DiagnosticsRedactor.Redact(string.IsNullOrWhiteSpace(title) ? "Untitled.sql" : title);
        safeTitle = safeTitle.Replace('\r', ' ').Replace('\n', ' ');
        if (safeTitle.Length > 120)
            safeTitle = safeTitle[..120];

        return new RecoveryDocument(
            id.Trim(),
            safeTitle,
            string.IsNullOrWhiteSpace(filePath) ? null : filePath,
            redacted,
            wasRedacted,
            truncated,
            capturedUtc.ToUniversalTime());
    }

    /// <summary>
    /// Keeps stored copies that are still unsaved. Replaces a copy when that tab is dirty again.
    /// Drops a copy when its id is open and clean, or when the caller has adopted it into an editor tab.
    /// </summary>
    public static IReadOnlyList<RecoveryDocument> Merge(
        IReadOnlyList<RecoveryDocument> stored,
        IReadOnlyList<RecoveryDocument> openDirty,
        IReadOnlyCollection<string> openCleanIds,
        IReadOnlyCollection<string> adoptedIds)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(openDirty);
        ArgumentNullException.ThrowIfNull(openCleanIds);
        ArgumentNullException.ThrowIfNull(adoptedIds);

        var clean = new HashSet<string>(openCleanIds, StringComparer.Ordinal);
        var adopted = new HashSet<string>(adoptedIds, StringComparer.Ordinal);
        var dirtyIds = new HashSet<string>(openDirty.Select(d => d.Id), StringComparer.Ordinal);
        var next = new List<RecoveryDocument>();
        foreach (var doc in stored)
        {
            if (adopted.Contains(doc.Id) || clean.Contains(doc.Id) || dirtyIds.Contains(doc.Id))
                continue;
            next.Add(doc);
        }

        next.AddRange(openDirty);
        if (next.Count > MaxDocuments)
            next.RemoveRange(0, next.Count - MaxDocuments);
        return next;
    }

    public static string OfferSummary(IReadOnlyList<RecoveryDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var count = documents.Count;
        var lead = count == 1
            ? "1 unsaved script is stored in Application Support on this Mac."
            : count.ToString(System.Globalization.CultureInfo.InvariantCulture)
              + " unsaved scripts are stored in Application Support on this Mac.";
        var text = lead
            + " Restore opens them as unsaved query tabs. Discard removes those copies. Closing this sheet keeps them.";
        if (documents.Any(d => d.TextWasRedacted))
            text += " Credential-like text was removed before the copy was stored.";
        if (documents.Any(d => d.TextWasTruncated))
            text += " A long script was shortened in the copy.";
        return text;
    }
}

public sealed class DocumentRecoveryStore
{
    readonly object _lock = new();
    readonly string _path;

    public DocumentRecoveryStore(string? rootDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(rootDirectory) ? AtomicFile.DefaultRoot() : rootDirectory;
        _path = Path.Combine(root, "recovery", "unsaved-documents.json");
    }

    public string FilePath => _path;

    public IReadOnlyList<RecoveryDocument> Read() => TryRead(out var documents) ? documents : [];

    /// <summary>False when a copy file exists but cannot be read; callers must then leave it in place.</summary>
    public bool TryRead(out IReadOnlyList<RecoveryDocument> documents)
    {
        lock (_lock)
        {
            documents = [];
            if (!File.Exists(_path))
                return true;
            try
            {
                documents = JsonSerializer.Deserialize<List<RecoveryDocument>>(File.ReadAllText(_path), AtomicFile.JsonOptions) ?? [];
                return true;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public void Write(IReadOnlyList<RecoveryDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        lock (_lock)
        {
            if (documents.Count == 0)
            {
                DeleteFile();
                return;
            }

            var json = JsonSerializer.Serialize(documents, AtomicFile.JsonOptions);
            AtomicFile.Write(_path, json);
        }
    }

    public void Clear()
    {
        lock (_lock)
            DeleteFile();
    }

    void DeleteFile()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}

/// <summary>One open tab captured on the UI thread. Strings are immutable, so later edits cannot change a queued snapshot.</summary>
public sealed record RecoveryTabState(string Id, string Title, string? FilePath, string Text, bool IsDirty);

/// <summary>
/// Debounced, serialised recovery persistence off the caller's thread. Only the newest queued snapshot is written,
/// one write at a time, so an older snapshot can never overwrite newer, saved, closed or discarded state.
/// Redaction and disk I/O run on the worker, not the UI thread.
/// </summary>
public sealed class RecoveryWriter : IDisposable
{
    readonly DocumentRecoveryStore _store;
    readonly TimeSpan _delay;
    readonly Action<string> _report;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _lock = new();
    readonly Timer _timer;
    Pending? _pending;
    bool _enabled;
    int _retentionDays = AppSettings.DefaultPrivateDataRetentionDays;

    sealed record Pending(RecoveryTabState[] Tabs, HashSet<string> Removed);

    /// <param name="report">Called on a worker thread with a user-facing message when a write cannot be completed.</param>
    public RecoveryWriter(DocumentRecoveryStore store, TimeSpan delay, Action<string> report, bool enabled)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _delay = delay;
        _enabled = enabled;
        _timer = new Timer(_ => _ = WriteLatestAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool Enabled
    {
        get { lock (_lock) return _enabled; }
    }

    /// <summary>Stored copies captured longer ago than this are dropped on the next write.</summary>
    public int RetentionDays
    {
        get => Volatile.Read(ref _retentionDays);
        set => Volatile.Write(ref _retentionDays, Math.Clamp(value, AppSettings.MinPrivateDataRetentionDays, AppSettings.MaxPrivateDataRetentionDays));
    }

    /// <summary>
    /// Queues the open tabs, replacing any queued snapshot. <paramref name="removedIds"/> are stored copies to drop
    /// (restored into another tab, or closed with Discard); they accumulate until written. No-op while disabled.
    /// </summary>
    public void Schedule(IEnumerable<RecoveryTabState> tabs, IEnumerable<string>? removedIds = null)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        var snapshot = tabs.ToArray();
        lock (_lock)
        {
            if (!_enabled)
                return;
            var removed = _pending?.Removed ?? new HashSet<string>(StringComparer.Ordinal);
            if (removedIds is not null)
                removed.UnionWith(removedIds);
            _pending = new Pending(snapshot, removed);
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes the queued snapshot now on a worker thread; completes after it and any in-flight write.</summary>
    public Task FlushAsync() => Task.Run(WriteLatestAsync);

    /// <summary>Deletes stored copies after any in-flight write. A snapshot queued later is still written while enabled.</summary>
    public Task ClearAsync() => Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _store.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _report("Could not delete the unsaved-script copies: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    });

    /// <summary>Turning recovery off drops the queued snapshot and deletes stored copies.</summary>
    public Task SetEnabledAsync(bool enabled)
    {
        lock (_lock)
        {
            _enabled = enabled;
            if (enabled)
                return Task.CompletedTask;
            _pending = null;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        return ClearAsync();
    }

    async Task WriteLatestAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Pending? next;
            lock (_lock)
            {
                next = _pending;
                _pending = null;
            }
            if (next is not null)
                Write(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    void Write(Pending next)
    {
        try
        {
            if (!_store.TryRead(out var stored))
            {
                _report("Unsaved script copies could not be read. They were left in place.");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddDays(-RetentionDays);
            var dirty = new List<RecoveryDocument>();
            var clean = new List<string>();
            foreach (var tab in next.Tabs)
            {
                if (tab.IsDirty)
                    dirty.Add(DocumentRecovery.Capture(tab.Id, tab.Title, tab.FilePath, tab.Text, now));
                else
                    clean.Add(tab.Id);
            }

            var current = stored.Where(d => d.CapturedUtc >= cutoff).ToList();
            _store.Write(DocumentRecovery.Merge(current, dirty, clean, next.Removed));
        }
        catch (Exception ex)
        {
            _report("Could not update the unsaved-script copy: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _gate.Dispose();
    }
}
