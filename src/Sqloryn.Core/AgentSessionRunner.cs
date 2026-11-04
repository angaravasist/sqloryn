namespace Sqloryn.Core;

/// <summary>Adapts a <see cref="SqlSession"/> to the Agent read and command seams. Adds no SQL of its own.</summary>
public sealed class AgentSessionRunner(SqlSession session) : IAgentQueryRunner, IAgentCommandRunner
{
    public Task<RawRows> QueryAsync(string sql, IReadOnlyList<EditParameter> parameters, int maxRows, CancellationToken cancellationToken)
        => session.QueryRawAsync(sql, parameters, maxRows, cancellationToken);

    public Task ExecuteAsync(AgentCommand command, CancellationToken cancellationToken)
        => session.ExecuteAgentCommandAsync(command, cancellationToken);
}
