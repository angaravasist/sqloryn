namespace Sqloryn.Core;

/// <summary>Changes the database context of one session. Other sessions are only observed.</summary>
public static class ActiveDatabaseSwitch
{
    public static async Task<DatabaseSwitchOutcome> ApplyAsync(
        IDatabaseSession active,
        IReadOnlyList<IDatabaseSession> others,
        string database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(others);
        if (string.IsNullOrWhiteSpace(database))
            return DatabaseSwitchOutcome.Refused("Choose a database from the list for this tab.");
        if (!active.IsConnected)
            return DatabaseSwitchOutcome.Refused(
                "Connect before changing the database. A new login is not requested.");

        var catalog = await active.ListDatabasesAsync(cancellationToken).ConfigureAwait(false);
        var match = catalog.FirstOrDefault(name => string.Equals(name, database, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return DatabaseSwitchOutcome.Refused(
                $"'{database.Trim()}' is not in the databases this login can access.");
        if (string.Equals(active.Database, match, StringComparison.OrdinalIgnoreCase))
            return new DatabaseSwitchOutcome(true, match,
                $"Database context is already {match} on this tab. The login was not re-entered. Other query tabs were not changed.");

        var before = others.Select(session => session.Database).ToArray();
        await active.ChangeDatabaseAsync(match, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < others.Count; i++)
        {
            if (!string.Equals(others[i].Database, before[i], StringComparison.Ordinal))
                throw new InvalidOperationException("Database switch changed another tab's session.");
        }
        return DatabaseSwitchOutcome.AppliedTo(match);
    }
}

public sealed record DatabaseSwitchOutcome(bool Applied, string? Database, string Message)
{
    public static DatabaseSwitchOutcome Refused(string message) => new(false, null, message);

    public static DatabaseSwitchOutcome AppliedTo(string database) => new(true, database,
        $"Database context is now {database} on this tab. The login was not re-entered. Other query tabs were not changed.");
}
