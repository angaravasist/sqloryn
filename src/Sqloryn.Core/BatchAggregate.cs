namespace Sqloryn.Core;

sealed class BatchAggregate
{
    readonly List<ResultSet> _sets = [];
    readonly List<QueryMessage> _messages = [];
    readonly List<string> _errors = [];
    long _retainedChars;
    QueryCompletionKind _completion = QueryCompletionKind.Succeeded;
    StopCause _stopCause = StopCause.None;
    bool _partial;
    bool _retentionLimit;
    int? _transactionState;
    TargetConfidence _target = TargetConfidence.Known;
    string? _database;
    readonly List<int?> _rowsAffected = [];
    long _clientElapsed;
    bool _hasClientElapsed;

    public void Add(QueryResult result, int index, int total)
    {
        foreach (var set in result.ResultSets)
        {
            var chars = CountChars(set);
            if (_sets.Count >= QueryLimits.MaxResultSets || _retainedChars + chars > QueryLimits.MaxRetainedOutputChars)
            {
                _retentionLimit = true;
                continue;
            }
            _retainedChars += chars;
            _sets.Add(set);
        }

        foreach (var message in result.Messages)
        {
            if (_messages.Count >= QueryLimits.MaxMessages)
                break;
            _messages.Add(message);
        }

        if (!string.IsNullOrEmpty(result.ErrorSummary))
            _errors.Add($"Batch {index} of {total}: {result.ErrorSummary}");
        if (result.Completion != QueryCompletionKind.Succeeded)
        {
            _completion = result.Completion;
            _stopCause = result.StopCause;
        }
        _partial |= result.PartialResults;
        _retentionLimit |= result.RetentionLimitReached;
        _transactionState = result.TransactionState ?? _transactionState;
        _target = result.TargetConfidence;
        _database = result.Database;
        _rowsAffected.Add(result.RowsAffected);
        if (result.ClientElapsedMilliseconds is long ms && ms >= 0)
        {
            _clientElapsed += ms;
            _hasClientElapsed = true;
        }
    }

    public void NoteSkipped(int completedIndex, int total)
    {
        var remaining = total - completedIndex;
        if (remaining > 0)
            _errors.Add($"{remaining} remaining batch(es) were not run.");
    }

    public void StopBeforeBatch(StopCause cause, int nextIndex, int total, string? database)
    {
        _completion = cause == StopCause.Disconnect
            ? QueryCompletionKind.UnknownAfterDisconnect
            : QueryCompletionKind.CancelledClientEnded;
        _stopCause = cause;
        if (cause == StopCause.Disconnect)
            _target = TargetConfidence.Unknown;
        _partial = true;
        var reason = cause == StopCause.Disconnect ? "Disconnected" : "Cancelled";
        _errors.Add($"{reason} before batch {nextIndex} of {total}; {total - nextIndex + 1} batch(es) were not run.");
        _database ??= database;
    }

    public void FailBeforeBatch(string message, int nextIndex, int total, string? database)
    {
        _completion = QueryCompletionKind.Failed;
        _stopCause = StopCause.Other;
        _partial = true;
        _errors.Add($"Batch {nextIndex} of {total} could not start: {message} {total - nextIndex + 1} batch(es) were not run.");
        _database ??= database;
    }

    public QueryResult Build(string executedSql, string server, string fallbackDatabase)
    {
        var database = _target == TargetConfidence.Unknown
            ? (string.IsNullOrEmpty(_database) ? "(unknown)" : _database)
            : (string.IsNullOrEmpty(_database) ? fallbackDatabase : _database);
        return new QueryResult
        {
            ExecutedSql = executedSql,
            Server = server,
            Database = database,
            Completion = _completion,
            ResultSets = _sets,
            Messages = _messages,
            ErrorSummary = _errors.Count == 0 ? null : string.Join(" ", _errors),
            PartialResults = _partial || _retentionLimit,
            TransactionState = _transactionState,
            TargetConfidence = _target,
            RetentionLimitReached = _retentionLimit,
            StopCause = _stopCause,
            RowsAffected = ProviderRowsAffected.SumReported(_rowsAffected),
            ClientElapsedMilliseconds = _hasClientElapsed ? _clientElapsed : null
        };
    }

    static long CountChars(ResultSet set)
    {
        long total = 0;
        foreach (var row in set.Rows)
            foreach (var cell in row)
                total += Math.Max(cell.Display.Length, QueryLimits.MinCellBudgetChars);
        return total;
    }
}
