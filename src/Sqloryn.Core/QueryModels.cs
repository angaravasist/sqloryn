namespace Sqloryn.Core;

public enum QueryCompletionKind
{
    Succeeded,
    Failed,
    /// <summary>User asked to cancel; client command has not yet ended.</summary>
    CancellationRequested,
    /// <summary>Client command ended due to cancel; server/transaction state was not verified.</summary>
    CancelledClientEnded,
    /// <summary>Client command ended due to cancel and a follow-up probe verified session/transaction state.</summary>
    CancelledServerVerified,
    TimedOut,
    UnknownAfterDisconnect,
    /// <summary>Display retention stopped; batch end was not observed within the drain deadline.</summary>
    FinalCompletionUnobserved
}

/// <summary>Actual stop cause at ExecuteReader / Read / NextResult. Distinct from target confidence.</summary>
public enum StopCause
{
    None,
    UserCancel,
    Disconnect,
    WatchdogTimeout,
    ProviderTimeout,
    DrainDeadline,
    TransportLoss,
    SqlError,
    Other
}

public enum TargetConfidence
{
    /// <summary>DB_NAME() (or connect identity) was verified for this session.</summary>
    Known,
    /// <summary>Target could not be verified after a batch; further execution is blocked until revalidation.</summary>
    Unknown
}

public sealed record QueryMessage(byte Class, int Number, string Text);

public sealed class ResultSet
{
    public required IReadOnlyList<string> Columns { get; init; }
    /// <summary>Provider type names aligned with <see cref="Columns"/> (may be empty strings when unknown).</summary>
    public IReadOnlyList<string> SqlTypes { get; init; } = [];
    public required IReadOnlyList<IReadOnlyList<CellValue>> Rows { get; init; }
    public bool Truncated { get; init; }
    public bool BudgetExceeded { get; init; }
}

public readonly record struct CellValue(bool IsNull, string Display, string Kind, bool Truncated = false)
{
    public static CellValue Null => new(true, "NULL", "null");
    public static CellValue Text(string value, string kind, bool truncated = false)
        => new(false, value, kind, truncated);

    /// <summary>Copy text. SQL NULL is empty, matching CSV, and is not the display word NULL.</summary>
    public string ClipboardText => ResultCopy.FormatCell(this);
}

public sealed class QueryResult
{
    public required string ExecutedSql { get; init; }
    public required string Server { get; init; }
    public required string Database { get; init; }
    public required QueryCompletionKind Completion { get; init; }
    public IReadOnlyList<ResultSet> ResultSets { get; init; } = [];
    public IReadOnlyList<QueryMessage> Messages { get; init; } = [];
    public string? ErrorSummary { get; init; }
    public bool PartialResults { get; init; }
    public int? TransactionState { get; init; }
    public TargetConfidence TargetConfidence { get; init; } = TargetConfidence.Known;
    /// <summary>True when retained output stopped early due to row/budget/result-set limits.</summary>
    public bool RetentionLimitReached { get; init; }
    /// <summary>Stop cause preserved from ExecuteReader / Read / NextResult. Never upgraded by target checks.</summary>
    public StopCause StopCause { get; init; }
    /// <summary>Provider-reported rows affected, when the command reported one. Null means unreported (not zero).</summary>
    public int? RowsAffected { get; init; }
    /// <summary>Client stopwatch around the batch execute, in milliseconds. Not a server statistic. Null means not measured.</summary>
    public long? ClientElapsedMilliseconds { get; init; }
}

public static class QueryLimits
{
    public const int MaxRowsPerSet = 5_000;
    public const int MaxCellChars = 4_096;
    public const int MaxResultSets = 20;
    public const int MaxMessages = 200;
    /// <summary>Aggregate retained display characters across all cells in one execution.</summary>
    public const int MaxRetainedOutputChars = 2_000_000;
    /// <summary>Minimum budget charge per cell slot (covers empty text and NULL).</summary>
    public const int MinCellBudgetChars = 1;
}

public static class SqlIdentity
{
    public static string QuoteIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 || name.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("Invalid identifier.");
        return "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    public static string Qualify(string? schema, string name)
        => string.IsNullOrEmpty(schema)
            ? QuoteIdentifier(name)
            : QuoteIdentifier(schema) + "." + QuoteIdentifier(name);

    public static string Qualify3(string database, string schema, string name)
        => QuoteIdentifier(database) + "." + Qualify(schema, name);

    public static string ScriptSelectTop(string database, string schema, string name, int top = 100)
        => $"SELECT TOP ({top}) * FROM {Qualify3(database, schema, name)};{Environment.NewLine}";
}

public static class ExecutionOutcomes
{
    public static bool IsProviderTimeout(int sqlErrorNumber) => sqlErrorNumber == -2;

    public static bool IsBrokenConnection(int sqlErrorNumber)
        => sqlErrorNumber is -1 or 233 or 10053 or 10054 or 64;

    public static QueryCompletionKind FromSqlError(int sqlErrorNumber)
    {
        if (IsProviderTimeout(sqlErrorNumber))
            return QueryCompletionKind.TimedOut;
        if (IsBrokenConnection(sqlErrorNumber))
            return QueryCompletionKind.UnknownAfterDisconnect;
        return QueryCompletionKind.Failed;
    }

    public static QueryCompletionKind FromCancellation(
        bool disconnectRequested,
        bool userCancellation,
        bool serverStateVerified)
    {
        if (disconnectRequested)
            return QueryCompletionKind.UnknownAfterDisconnect;
        if (!userCancellation)
            return QueryCompletionKind.TimedOut;
        return serverStateVerified
            ? QueryCompletionKind.CancelledServerVerified
            : QueryCompletionKind.CancelledClientEnded;
    }
}

public readonly record struct OperationBinding(
    Guid TabId,
    object SessionKey,
    ScriptDocument Document,
    string? Database,
    long Generation)
{
    public bool Matches(
        Guid tabId,
        object sessionKey,
        ScriptDocument document,
        string? database,
        long generation,
        bool tabOpen)
        => tabOpen
           && TabId == tabId
           && ReferenceEquals(SessionKey, sessionKey)
           && ReferenceEquals(Document, document)
           && string.Equals(Database, database, StringComparison.Ordinal)
           && Generation == generation;
}
