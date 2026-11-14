namespace Sqloryn.Core;

/// <summary>
/// Database catalog and context for one query-tab session.
/// Changing the database does not take credentials; the existing login stays.
/// </summary>
public interface IDatabaseSession
{
    bool IsConnected { get; }
    string? Database { get; }
    Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken cancellationToken = default);
    Task ChangeDatabaseAsync(string database, CancellationToken cancellationToken = default);
}
