using Sqloryn.Core;

/// <summary>
/// Models SHOWPLAN semantics without SQL Server. <see cref="DmlExecutions"/> is an independent counter: it increments only
/// when a batch is delivered while SHOWPLAN is OFF, i.e. when the "server" would really have executed the statement.
/// </summary>
sealed class FakePlanRunner : IPlanCommandRunner
{
    public List<string> Log { get; } = [];
    public bool ShowplanOn { get; private set; }
    public int DmlExecutions { get; private set; }
    public int BatchCalls { get; private set; }
    public Exception? OnFailure { get; set; }
    public Exception? OffFailure { get; set; }
    public Exception? BatchFailure { get; set; }
    public Func<string, IReadOnlyList<string>>? PlanFor { get; set; }

    public Task SetShowplanXmlAsync(bool on, CancellationToken cancellationToken)
    {
        Log.Add(on ? "SET ON" : "SET OFF");
        var failure = on ? OnFailure : OffFailure;
        if (failure is not null)
            throw failure;
        ShowplanOn = on;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> RunBatchAsync(string batch, CancellationToken cancellationToken)
    {
        BatchCalls++;
        Log.Add("BATCH " + batch.Trim());
        cancellationToken.ThrowIfCancellationRequested();
        if (!ShowplanOn)
        {
            // The server would execute this for real.
            DmlExecutions++;
            return Task.FromResult<IReadOnlyList<string>>(["EXECUTED"]);
        }
        if (BatchFailure is not null)
            throw BatchFailure;
        return Task.FromResult(PlanFor?.Invoke(batch) ?? ["<ShowPlanXML>" + batch.Trim().Length + "</ShowPlanXML>"]);
    }
}
