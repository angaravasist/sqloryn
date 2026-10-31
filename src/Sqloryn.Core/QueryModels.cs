namespace Sqloryn.Core;

public enum QueryCompletionKind
{
    Succeeded,
    Failed,
    CancelledConfirmed,
    TimedOut,
    UnknownAfterDisconnect
}

public sealed record QueryMessage(byte Class, int Number, string Text);

public sealed class ResultSet
{
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<CellValue>> Rows { get; init; }
    public bool Truncated { get; init; }
}

public readonly record struct CellValue(bool IsNull, string Display, string Kind)
{
    public static CellValue Null => new(true, "NULL", "null");
    public static CellValue Text(string value, string kind) => new(false, value, kind);
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
}

public static class QueryLimits
{
    public const int MaxRowsPerSet = 5_000;
    public const int MaxCellChars = 4_096;
    public const int MaxResultSets = 20;
    public const int MaxMessages = 200;
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
}
