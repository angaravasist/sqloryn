using Sqloryn.Core;

/// <summary>Scripted catalog runner: records every query and returns canned rows or throws. No SQL Server.</summary>
sealed class FakeSecurityRunner(Func<string, RawRows>? respond = null, Exception? fail = null) : ISecurityQueryRunner
{
    public List<(string Sql, IReadOnlyList<EditParameter> Parameters, int MaxRows)> Calls { get; } = [];

    public Task<RawRows> QueryAsync(string sql, IReadOnlyList<EditParameter> parameters, int maxRows, CancellationToken cancellationToken)
    {
        Calls.Add((sql, parameters, maxRows));
        if (fail is not null)
            throw fail;
        return Task.FromResult(respond is null ? Rows() : respond(sql));
    }

    public static RawRows Rows(bool truncated = false, bool byteBudget = false, params object?[][] rows)
        => new([], [], rows, truncated, byteBudget);
}

/// <summary>
/// In-memory permission transaction: statements become durable only on commit, and rollback discards them.
/// Models the server-side fact that a rolled-back transaction leaves no GRANT/DENY/REVOKE applied.
/// </summary>
sealed class FakePermissionTransactionFactory : IPermissionTransactionFactory
{
    readonly Queue<object?> _script;
    public List<string> Log { get; } = [];
    public List<string> Executed { get; } = [];
    public List<string> Durable { get; } = [];
    public Exception? BeginFails { get; init; }
    public Exception? CommitFails { get; init; }
    public Exception? RollbackFails { get; init; }
    public Exception? DisposeFails { get; init; }

    /// <summary>One entry per statement: null = success, Exception = thrown by that statement.</summary>
    public FakePermissionTransactionFactory(params object?[] script) => _script = new Queue<object?>(script);

    public Task<IPermissionTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        if (BeginFails is not null)
            throw BeginFails;
        Log.Add("begin");
        return Task.FromResult<IPermissionTransaction>(new Tx(this));
    }

    sealed class Tx(FakePermissionTransactionFactory owner) : IPermissionTransaction
    {
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
