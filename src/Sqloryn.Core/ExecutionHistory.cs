using System.Text.Json;

namespace Sqloryn.Core;

/// <summary>
/// One finished batch remembered on this Mac. SQL text, outcome and client elapsed time only.
/// Result rows, messages, errors and the server name are not stored.
/// </summary>
public sealed record ExecutionHistoryEntry(
    string Sql,
    string Outcome,
    long? ElapsedMilliseconds,
    DateTimeOffset RecordedUtc);

public static class ExecutionHistory
{
    public const int MaxEntries = 40;
    public const int MaxSqlChars = 4_000;

    public static ExecutionHistoryEntry FromResult(QueryResult result, DateTimeOffset recordedUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        return RememberBatch(result.ExecutedSql, result.ClientElapsedMilliseconds, recordedUtc, QueryOutcomeFormatter.DescribeStatus(result));
    }

    public static ExecutionHistoryEntry FromBatch(
        string sql,
        QueryCompletionKind completion,
        long? elapsedMilliseconds,
        DateTimeOffset recordedUtc)
    {
        var outcome = QueryOutcomeFormatter.DescribeStatus(new QueryResult
        {
            ExecutedSql = "",
            Server = "",
            Database = "",
            Completion = completion
        });
        return RememberBatch(sql, elapsedMilliseconds, recordedUtc, outcome);
    }

    public static IReadOnlyList<ExecutionHistoryEntry> Remember(
        IReadOnlyList<ExecutionHistoryEntry> existing,
        ExecutionHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(entry);
        var next = new List<ExecutionHistoryEntry>(Math.Min(existing.Count + 1, MaxEntries)) { entry };
        foreach (var older in existing)
        {
            if (next.Count == MaxEntries)
                break;
            next.Add(older);
        }
        return next;
    }

    /// <summary>SQL to put in the editor. This does not run it.</summary>
    public static string ScriptForEditor(ExecutionHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Sql;
    }

    static ExecutionHistoryEntry RememberBatch(
        string sql,
        long? elapsedMilliseconds,
        DateTimeOffset recordedUtc,
        string outcome)
    {
        var redacted = DiagnosticsRedactor.Redact(sql ?? "");
        if (redacted.Length > MaxSqlChars)
            redacted = redacted[..MaxSqlChars] + "\n-- truncated; the stored copy is shorter than the batch that ran.";
        long? elapsed = elapsedMilliseconds is null or < 0 ? null : elapsedMilliseconds;
        return new ExecutionHistoryEntry(redacted, outcome, elapsed, recordedUtc.ToUniversalTime());
    }
}

public sealed class ExecutionHistoryStore
{
    readonly object _lock = new();
    readonly string _path;

    public ExecutionHistoryStore(string? rootDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(rootDirectory) ? AtomicFile.DefaultRoot() : rootDirectory;
        _path = Path.Combine(root, "history", "batches.json");
    }

    int _retentionDays = AppSettings.DefaultPrivateDataRetentionDays;

    public string FilePath => _path;

    /// <summary>Entries recorded longer ago than this are not returned and are dropped on the next append.</summary>
    public int RetentionDays
    {
        get => Volatile.Read(ref _retentionDays);
        set => Volatile.Write(ref _retentionDays, Math.Clamp(value, AppSettings.MinPrivateDataRetentionDays, AppSettings.MaxPrivateDataRetentionDays));
    }

    public IReadOnlyList<ExecutionHistoryEntry> Read()
    {
        lock (_lock)
            return ReadHoldingLock();
    }

    public void Append(ExecutionHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            var next = ExecutionHistory.Remember(ReadHoldingLock(), entry);
            AtomicFile.Write(_path, JsonSerializer.Serialize(next, AtomicFile.JsonOptions));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
    }

    List<ExecutionHistoryEntry> ReadHoldingLock()
    {
        if (!File.Exists(_path))
            return [];
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
            var entries = JsonSerializer.Deserialize<List<ExecutionHistoryEntry>>(File.ReadAllText(_path), AtomicFile.JsonOptions) ?? [];
            return entries.Where(e => e.RecordedUtc >= cutoff).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
