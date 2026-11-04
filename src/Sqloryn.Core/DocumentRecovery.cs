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

    public IReadOnlyList<RecoveryDocument> Read()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
                return [];
            try
            {
                var documents = JsonSerializer.Deserialize<List<RecoveryDocument>>(File.ReadAllText(_path), AtomicFile.JsonOptions);
                return documents ?? [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return [];
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
