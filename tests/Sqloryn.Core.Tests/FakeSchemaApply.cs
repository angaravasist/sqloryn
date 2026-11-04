using Sqloryn.Core;

/// <summary>
/// In-memory stand-in for one schema transaction. Records every call and models the server-side fact that a rolled-back
/// transaction leaves no statement applied. No SQL Server involved.
/// </summary>
sealed class FakeSchemaTransactionFactory : ISchemaTransactionFactory
{
    readonly Queue<object?> _script;
    public List<string> Log { get; } = [];
    public List<string> Executed { get; } = [];
    /// <summary>Statements that survived: filled on commit, cleared on rollback.</summary>
    public List<string> Durable { get; } = [];
    public string Fingerprint { get; init; } = "fp-1";
    public Exception? FingerprintFails { get; init; }
    public Exception? BeginFails { get; init; }
    public Exception? CommitFails { get; init; }
    public Exception? RollbackFails { get; init; }
    public Exception? DisposeFails { get; init; }

    /// <summary>One entry per statement: null = success, Exception = thrown by that statement.</summary>
    public FakeSchemaTransactionFactory(params object?[] script) => _script = new Queue<object?>(script);

    public Task<ISchemaTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        if (BeginFails is not null)
            throw BeginFails;
        Log.Add("begin");
        return Task.FromResult<ISchemaTransaction>(new Tx(this));
    }

    sealed class Tx(FakeSchemaTransactionFactory owner) : ISchemaTransaction
    {
        public Task<string> ReadFingerprintAsync(TableRef table, CancellationToken cancellationToken)
        {
            owner.Log.Add("fingerprint");
            if (owner.FingerprintFails is not null)
                throw owner.FingerprintFails;
            return Task.FromResult(owner.Fingerprint);
        }

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.Log.Add("exec");
            var next = owner._script.Count > 0 ? owner._script.Dequeue() : null;
            if (next is Exception ex)
                throw ex;
            owner.Executed.Add(sql);
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            if (owner.CommitFails is not null)
                throw owner.CommitFails;
            owner.Log.Add("commit");
            owner.Durable.AddRange(owner.Executed);
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            owner.Log.Add("rollback");
            if (owner.RollbackFails is not null)
                throw owner.RollbackFails;
            owner.Durable.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            owner.Log.Add("dispose");
            if (owner.DisposeFails is not null)
                throw owner.DisposeFails;
            return ValueTask.CompletedTask;
        }
    }
}
