using Sqloryn.Core;

/// <summary>In-memory import transaction: records calls, models "durable only after commit", replays scripted failures. No SQL Server.</summary>
sealed class FakeCsvImportFactory : ICsvImportTransactionFactory
{
    readonly int _replacedRows;
    readonly int _failOnExec;
    readonly Exception? _failWith;
    readonly Action<int>? _onExec;
    readonly int? _insertCountOverride;
    readonly Exception? _commitFails;
    readonly Exception? _rollbackFails;
    readonly Exception? _beginFails;
    readonly Exception? _disposeFails;
    int _execCalls;
    int _pendingRows;

    public FakeCsvImportFactory(
        int replacedRows = 0,
        int failOnExec = 0,
        Exception? failWith = null,
        Action<int>? onExec = null,
        int? insertCountOverride = null,
        Exception? commitFails = null,
        Exception? rollbackFails = null,
        Exception? beginFails = null,
        Exception? disposeFails = null)
    {
        _replacedRows = replacedRows;
        _failOnExec = failOnExec;
        _failWith = failWith;
        _onExec = onExec;
        _insertCountOverride = insertCountOverride;
        _commitFails = commitFails;
        _rollbackFails = rollbackFails;
        _beginFails = beginFails;
        _disposeFails = disposeFails;
    }

    public List<string> Log { get; } = [];
    public List<EditCommand> Executed { get; } = [];
    public List<string> Ddl { get; } = [];
    public CancellationToken? RollbackToken { get; private set; }

    /// <summary>Rows made durable by a commit (the destination's observable change).</summary>
    public int Committed { get; private set; }

    public Task<ICsvImportTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        if (_beginFails is not null)
            throw _beginFails;
        Log.Add("begin");
        return Task.FromResult<ICsvImportTransaction>(new Tx(this));
    }

    sealed class Tx(FakeCsvImportFactory owner) : ICsvImportTransaction
    {
        public Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.Log.Add("ddl");
            owner.Ddl.Add(sql);
            return Task.CompletedTask;
        }

        public Task<int> ExecuteAffectedAsync(EditCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner._execCalls++;
            owner.Log.Add("exec");
            owner.Executed.Add(command);
            owner._onExec?.Invoke(owner._execCalls);
            if (owner._failOnExec == owner._execCalls && owner._failWith is not null)
                throw owner._failWith;
            if (command.CommandText.Contains("DELETE FROM", StringComparison.Ordinal))
                return Task.FromResult(owner._replacedRows);
            var rows = command.CommandText.Split("(@p").Length - 1;
            owner._pendingRows += rows;
            return Task.FromResult(owner._insertCountOverride ?? rows);
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            if (owner._commitFails is not null)
                throw owner._commitFails;
            owner.Log.Add("commit");
            owner.Committed += owner._pendingRows;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            owner.RollbackToken = cancellationToken;
            owner.Log.Add("rollback");
            owner._pendingRows = 0;
            if (owner._rollbackFails is not null)
                throw owner._rollbackFails;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            owner.Log.Add("dispose");
            if (owner._disposeFails is not null)
                throw owner._disposeFails;
            return ValueTask.CompletedTask;
        }
    }
}
