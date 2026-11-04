using System.Globalization;
using System.Text;

namespace Sqloryn.Core;

public enum PermissionAction { Grant, Deny, Revoke }

public enum PermissionScope
{
    /// <summary>Server-level permission on a login or server role. Runs on the session's connection as is.</summary>
    Server,
    /// <summary>Database-level permission on a database user or role.</summary>
    Database,
    /// <summary>Permission on one schema.</summary>
    Schema,
    /// <summary>Permission on one object (optionally limited to columns).</summary>
    Object
}

/// <summary>
/// Structured GRANT/DENY/REVOKE request. It deliberately has no member that can carry a password, key or other secret:
/// this feature changes permissions only, never credentials.
/// </summary>
/// <param name="Permission">Must be one of <see cref="PermissionChangePreview.AllowedPermissions"/> for the scope (never free text).</param>
/// <param name="WithGrantOption">GRANT: <c>WITH GRANT OPTION</c>. REVOKE: <c>GRANT OPTION FOR</c>. Invalid for DENY.</param>
/// <param name="Cascade">DENY/REVOKE only: <c>CASCADE</c> also affects principals this principal granted to.</param>
public sealed record PermissionChange(
    PermissionAction Action,
    PermissionScope Scope,
    string Permission,
    string Principal,
    string? Database = null,
    string? Schema = null,
    string? ObjectName = null,
    IReadOnlyList<string>? Columns = null,
    bool WithGrantOption = false,
    bool Cascade = false);

/// <param name="Sql">Exactly what Apply runs for this change. Database-scoped changes are carried by <c>sp_executesql</c> in the target database, so no <c>USE</c> or connection-context change is needed.</param>
/// <param name="Description">Single-line, display-only summary. Never executed.</param>
public sealed record PermissionStatement(string Sql, string Description);

/// <summary>
/// The reviewed script for a set of permission changes. Building one is a pure function: it reads and writes nothing, so a
/// preview always leaves permissions unchanged. Only a preview built here can be given to <see cref="PermissionChangeApply"/>.
/// </summary>
public sealed class PermissionChangePreview
{
    const int MaxChanges = 200;

    static readonly string[] ServerPermissions =
    [
        "ALTER ANY LOGIN", "ALTER ANY DATABASE", "CONNECT SQL", "CONTROL SERVER", "VIEW ANY DATABASE", "VIEW ANY DEFINITION",
        "VIEW SERVER STATE"
    ];

    static readonly string[] DatabasePermissions =
    [
        "ALTER", "ALTER ANY ROLE", "ALTER ANY USER", "CONNECT", "CONTROL", "CREATE PROCEDURE", "CREATE TABLE", "CREATE VIEW",
        "DELETE", "EXECUTE", "INSERT", "REFERENCES", "SELECT", "SHOWPLAN", "UPDATE", "VIEW DATABASE STATE", "VIEW DEFINITION"
    ];

    static readonly string[] SecurablePermissions =
        ["ALTER", "CONTROL", "DELETE", "EXECUTE", "INSERT", "REFERENCES", "SELECT", "UPDATE", "VIEW DEFINITION"];

    static readonly HashSet<string> ColumnPermissions = new(StringComparer.Ordinal) { "SELECT", "UPDATE", "REFERENCES" };

    // Granting any of these (or any WITH GRANT OPTION) widens who can change security.
    static readonly HashSet<string> Elevated = new(StringComparer.Ordinal)
    {
        "CONTROL", "CONTROL SERVER", "ALTER", "ALTER ANY LOGIN", "ALTER ANY DATABASE", "ALTER ANY ROLE", "ALTER ANY USER"
    };

    PermissionChangePreview(IReadOnlyList<PermissionStatement> statements, IReadOnlyList<string> warnings)
    {
        Statements = statements;
        Warnings = warnings;
    }

    public IReadOnlyList<PermissionStatement> Statements { get; }

    /// <summary>Display-only cautions. Never part of <see cref="ExecutableSql"/>.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public bool HasChanges => Statements.Count > 0;

    /// <summary>One statement per line; the only text Apply is allowed to run.</summary>
    public string ExecutableSql => string.Join('\n', Statements.Select(s => s.Sql));

    public static IReadOnlyList<string> AllowedPermissions(PermissionScope scope) => scope switch
    {
        PermissionScope.Server => ServerPermissions,
        PermissionScope.Database => DatabasePermissions,
        PermissionScope.Schema or PermissionScope.Object => SecurablePermissions,
        _ => []
    };

    /// <param name="currentPrincipal">The login/user the session is connected as, so a change to it can be called out as a lock-out risk.</param>
    /// <exception cref="ArgumentException">A change is invalid, uses a permission outside the allow-list or carries an unsafe name.</exception>
    public static PermissionChangePreview Create(IReadOnlyList<PermissionChange> changes, string? currentPrincipal = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
            throw new ArgumentException("No permission changes to preview.", nameof(changes));
        if (changes.Count > MaxChanges)
            throw new ArgumentException($"At most {MaxChanges} permission changes can be previewed at once.", nameof(changes));

        var statements = new List<PermissionStatement>(changes.Count);
        var warnings = new List<string>();
        void Warn(string text)
        {
            if (!warnings.Contains(text))
                warnings.Add(text);
        }

        foreach (var change in changes)
        {
            ArgumentNullException.ThrowIfNull(change);
            statements.Add(Build(change));

            if (change.Action == PermissionAction.Grant)
            {
                if (Elevated.Contains(Canonical(change.Permission)) || change.WithGrantOption)
                    Warn("Grants an elevated permission or the right to grant it onwards. Confirm the principal and scope are intended.");
            }
            else if (change.Action == PermissionAction.Deny)
            {
                Warn("DENY overrides GRANT, including permissions the principal inherits through role membership.");
            }
            else
            {
                Warn("REVOKE removes only the entry set directly on this principal; permissions inherited through role membership remain.");
            }

            if (change.Action != PermissionAction.Grant && currentPrincipal is { Length: > 0 } me &&
                string.Equals(me, change.Principal, StringComparison.OrdinalIgnoreCase))
                Warn("This changes permissions of the principal you are connected as. A mistake can lock this session out.");
        }

        return new PermissionChangePreview(statements, warnings);
    }

    static string Canonical(string? permission)
        => string.Join(' ', (permission ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();

    /// <summary>Brackets are not enough on their own: control characters (including newlines) are refused so a name cannot start a new line of script.</summary>
    static string Quote(string? name, string label)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException($"{label} is required.");
        foreach (var ch in name)
            if (char.IsControl(ch) || ch is '\u2028' or '\u2029')
                throw new ArgumentException($"{label} contains a control character.");
        try
        {
            return SqlIdentity.QuoteIdentifier(name);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException($"{label} is not a valid identifier (1 to 128 characters).");
        }
    }

    static PermissionStatement Build(PermissionChange c)
    {
        var permission = Canonical(c.Permission);
        if (Array.IndexOf(AllowedPermissions(c.Scope).ToArray(), permission) < 0)
            throw new ArgumentException($"'{Sanitize(c.Permission)}' is not a supported {c.Scope.ToString().ToLowerInvariant()}-level permission.");
        if (!Enum.IsDefined(c.Action) || !Enum.IsDefined(c.Scope))
            throw new ArgumentException("Unknown action or scope.");
        if (c.Action == PermissionAction.Deny && c.WithGrantOption)
            throw new ArgumentException("DENY has no grant option.");
        if (c.Action == PermissionAction.Grant && c.Cascade)
            throw new ArgumentException("GRANT cannot cascade; CASCADE applies to DENY and REVOKE.");

        var principal = Quote(c.Principal, "Principal");
        var database = c.Scope == PermissionScope.Server ? null : Quote(c.Database, "Database");

        string? columns = null;
        if (c.Columns is not null)
        {
            if (c.Scope != PermissionScope.Object)
                throw new ArgumentException("A column list is only valid for an object permission.");
            if (!ColumnPermissions.Contains(permission))
                throw new ArgumentException("A column list is only valid for SELECT, UPDATE and REFERENCES.");
            if (c.Columns.Count == 0)
                throw new ArgumentException("The column list is empty.");
            columns = " (" + string.Join(", ", c.Columns.Select(col => Quote(col, "Column"))) + ")";
        }

        string on;
        string where;
        switch (c.Scope)
        {
            case PermissionScope.Server:
                on = "";
                where = "on the server";
                break;
            case PermissionScope.Database:
                on = "";
                where = "in " + database;
                break;
            case PermissionScope.Schema:
                var schema = Quote(c.Schema, "Schema");
                on = " ON SCHEMA::" + schema;
                where = $"on schema {schema} in {database}";
                break;
            default:
                var objectSchema = Quote(c.Schema, "Schema");
                var objectName = Quote(c.ObjectName, "Object name");
                on = $" ON OBJECT::{objectSchema}.{objectName}";
                where = $"on {objectSchema}.{objectName} in {database}";
                break;
        }

        var sb = new StringBuilder();
        switch (c.Action)
        {
            case PermissionAction.Grant:
                sb.Append("GRANT ").Append(permission).Append(columns).Append(on).Append(" TO ").Append(principal);
                if (c.WithGrantOption)
                    sb.Append(" WITH GRANT OPTION");
                break;
            case PermissionAction.Deny:
                sb.Append("DENY ").Append(permission).Append(columns).Append(on).Append(" TO ").Append(principal);
                if (c.Cascade)
                    sb.Append(" CASCADE");
                break;
            default:
                sb.Append("REVOKE ");
                if (c.WithGrantOption)
                    sb.Append("GRANT OPTION FOR ");
                sb.Append(permission).Append(columns).Append(on).Append(" FROM ").Append(principal);
                if (c.Cascade)
                    sb.Append(" CASCADE");
                break;
        }
        sb.Append(';');
        var inner = sb.ToString();

        // The whole statement travels as one N'...' literal: every ' in an identifier is doubled again here.
        var sql = database is null
            ? inner
            : $"EXEC {database}.sys.sp_executesql N'{inner.Replace("'", "''", StringComparison.Ordinal)}';";

        var verb = c.Action.ToString().ToUpperInvariant();
        var preposition = c.Action == PermissionAction.Revoke ? "from" : "to";
        var grantOption = c.WithGrantOption
            ? (c.Action == PermissionAction.Grant ? " with grant option" : " (grant option only)")
            : "";
        var description = $"{verb} {permission}{columns} {where} {preposition} {principal}{grantOption}{(c.Cascade ? " cascade" : "")}";
        return new PermissionStatement(sql, SchemaObjectCatalog.OneLine(description));
    }

    static string Sanitize(string? value)
    {
        var line = SchemaObjectCatalog.OneLine(value ?? "");
        return line.Length <= 60 ? line : line[..60] + "…";
    }
}
