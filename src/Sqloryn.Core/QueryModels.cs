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
    UnknownAfterDisconnect
}

public sealed record QueryMessage(byte Class, int Number, string Text);

public sealed class ResultSet
{
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<CellValue>> Rows { get; init; }
    public bool Truncated { get; init; }
    public bool BudgetExceeded { get; init; }
}

public readonly record struct CellValue(bool IsNull, string Display, string Kind, bool Truncated = false)
{
    public static CellValue Null => new(true, "NULL", "null");
    public static CellValue Text(string value, string kind, bool truncated = false)
        => new(false, value, kind, truncated);
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
    /// <summary>When cancel completed with a server probe, reports XACT_STATE() or null if unknown.</summary>
    public int? TransactionState { get; init; }
}

public static class QueryLimits
{
    public const int MaxRowsPerSet = 5_000;
    public const int MaxCellChars = 4_096;
    public const int MaxResultSets = 20;
    public const int MaxMessages = 200;
    /// <summary>Aggregate retained display characters across all cells in one execution.</summary>
    public const int MaxRetainedOutputChars = 2_000_000;
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

    /// <summary>Fully qualified database.schema.object for Explorer-generated scripts.</summary>
    public static string Qualify3(string database, string schema, string name)
        => QuoteIdentifier(database) + "." + Qualify(schema, name);

    public static string ScriptSelectTop(string database, string schema, string name, int top = 100)
        => $"SELECT TOP ({top}) * FROM {Qualify3(database, schema, name)};{Environment.NewLine}";
}

/// <summary>Classifies provider errors without replaying SQL.</summary>
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

/// <summary>Binds an async UI completion to the tab/session/document/database that started it.</summary>
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
