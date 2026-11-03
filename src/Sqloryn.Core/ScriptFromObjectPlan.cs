namespace Sqloryn.Core;

/// <summary>
/// Decision for "script this Object Explorer item": always a NEW tab containing only the generated
/// SQL, so the originating document, dirty state and selection are never mutated.
/// </summary>
/// <param name="NewTab">Always true; scripting never writes into an existing document.</param>
/// <param name="Sql">Generated SQL only (no existing editor text).</param>
/// <param name="TargetDatabase">The database the object belongs to; the new tab's session must target it.</param>
/// <param name="RequiresDatabaseChange">The originating session's database is not (verifiably) the target.</param>
public sealed record ScriptFromObjectPlan(bool NewTab, string Sql, string TargetDatabase, bool RequiresDatabaseChange)
{
    public static ScriptFromObjectPlan Create(
        string? sessionDatabase,
        bool sessionTargetUnknown,
        string? objectDatabase,
        string? schema,
        string? objectName)
    {
        if (string.IsNullOrWhiteSpace(objectDatabase) || string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(objectName))
            throw new ArgumentException("Incomplete object identity.");
        var requiresChange = sessionTargetUnknown
                             || string.IsNullOrEmpty(sessionDatabase)
                             || !string.Equals(sessionDatabase, objectDatabase, StringComparison.OrdinalIgnoreCase);
        return new ScriptFromObjectPlan(
            NewTab: true,
            Sql: SqlIdentity.ScriptSelectTop(objectDatabase, schema, objectName),
            TargetDatabase: objectDatabase,
            RequiresDatabaseChange: requiresChange);
    }
}

/// <summary>Honest Object Explorer wording when a catalog read stopped before the result ended.</summary>
public static class CatalogTruncation
{
    /// <summary>Null when the listing is complete; otherwise a user-facing note.</summary>
    public static string? Describe(int shown, bool truncated, bool byteBudgetExhausted)
    {
        if (!truncated)
            return null;
        var count = shown.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return byteBudgetExhausted
            ? $"Listing truncated: stopped after {count} items (load size budget reached). More items exist."
            : $"Listing truncated: showing first {count} items. More items exist.";
    }
}
