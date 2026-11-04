using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Sqloryn.Core;

/// <summary>Runs one parameterised, read-only catalog query and returns its first result set. Implemented over <see cref="SqlSession"/>; faked in tests.</summary>
public interface ISecurityQueryRunner
{
    Task<RawRows> QueryAsync(string sql, IReadOnlyList<EditParameter> parameters, int maxRows, CancellationToken cancellationToken);
}

/// <summary><see cref="ISecurityQueryRunner"/> over a session's raw, bounded query path. Reads never open a transaction.</summary>
public sealed class SqlSessionSecurityRunner(SqlSession session) : ISecurityQueryRunner
{
    public Task<RawRows> QueryAsync(string sql, IReadOnlyList<EditParameter> parameters, int maxRows, CancellationToken cancellationToken)
        => session.QueryRawAsync(sql, parameters, maxRows, cancellationToken);
}

/// <summary>
/// One security catalog read: the items, whether the row cap or byte budget stopped it early, or a mapped failure.
/// A failed or denied read is never an empty list: <see cref="Failure"/> is set and <see cref="Items"/> is empty.
/// </summary>
public sealed record SecurityListing<T>(IReadOnlyList<T> Items, bool Truncated, bool ByteBudgetExhausted, CatalogFailure? Failure)
{
    public bool Succeeded => Failure is null;

    /// <summary>User-facing note: the failure text, the truncation note, or null for a complete read.</summary>
    public string? Note => Failure?.Text ?? CatalogTruncation.Describe(Items.Count, Truncated, ByteBudgetExhausted);

    public static SecurityListing<T> Failed(CatalogFailure failure) => new([], false, false, failure);
}

/// <summary>A server-level principal that can log in. The type text is the server's own (no domain or tenant claim is derived from it).</summary>
public sealed record ServerLogin(string Name, string TypeDescription, bool IsDisabled, string? DefaultDatabase);

/// <summary>
/// A database user. <see cref="MappedLogin"/> is the server login with the same SID <i>as visible to the connected principal</i>:
/// SQL Server hides principals the caller may not see, so <see cref="HasNoVisibleLogin"/> is a hint, not proof of an orphan.
/// </summary>
public sealed record DatabaseUser(string Name, string TypeDescription, string AuthenticationType, string? DefaultSchema, string? MappedLogin)
{
    static readonly HashSet<string> Special = new(StringComparer.OrdinalIgnoreCase) { "dbo", "guest", "INFORMATION_SCHEMA", "sys" };

    /// <summary>Contained (database-authenticated) users have no server login by design.</summary>
    public bool IsContained => string.Equals(AuthenticationType, "DATABASE", StringComparison.OrdinalIgnoreCase);

    public bool HasNoVisibleLogin => MappedLogin is null &&
        string.Equals(AuthenticationType, "INSTANCE", StringComparison.OrdinalIgnoreCase) && !Special.Contains(Name);
}

/// <summary>One role/member pair. A role with no members has a null <see cref="Member"/> so it is still listed.</summary>
public sealed record RoleMembership(string Role, bool IsFixedRole, string? Member, string? MemberType);

/// <summary>One explicit permission entry on a principal (not the effective permission after role inheritance).</summary>
public sealed record GrantedPermission(string Scope, string Permission, string State, string? Securable);

/// <summary>
/// Read-only security catalog queries and row parsers. Database names are quoted identifiers (they cannot be parameters);
/// principal names are bound through <see cref="PrincipalParameter"/>. No query reads password hashes or credential
/// material, and none writes. SQL Server filters these catalog views by the caller's metadata visibility, so a short or
/// empty list can mean "not visible to you"; the UI says so.
/// </summary>
public static class SecurityCatalog
{
    /// <summary>Row cap for every security listing (the reader marks the listing truncated beyond it).</summary>
    public const int MaxRows = 2_000;

    public const string PrincipalParameter = "@principal";

    // S SQL login, U Windows login, G Windows group, E/X external (Entra) login/group, C certificate login, K asymmetric-key login.
    const string LoginTypes = "'S', 'U', 'G', 'E', 'X', 'C', 'K'";

    /// <summary>One column: the login name this session is connected as (used only to warn about self lock-out).</summary>
    public static string CurrentPrincipal() => "SELECT SUSER_SNAME();";

    /// <summary>Columns: name, type_desc, is_disabled, default_database_name.</summary>
    public static string ListServerLogins()
        => $"""
            SELECT name, type_desc, CONVERT(bit, is_disabled), default_database_name
            FROM sys.server_principals
            WHERE type IN ({LoginTypes})
            ORDER BY name;
            """;

    /// <summary>Columns: role name, is_fixed_role, member name (NULL for an empty role), member type_desc.</summary>
    public static string ListServerRoleMembers()
        => """
           SELECT r.name, CONVERT(bit, r.is_fixed_role), m.name, m.type_desc
           FROM sys.server_principals AS r
           LEFT JOIN sys.server_role_members AS rm ON rm.role_principal_id = r.principal_id
           LEFT JOIN sys.server_principals AS m ON m.principal_id = rm.member_principal_id
           WHERE r.type = 'R'
           ORDER BY r.name, m.name;
           """;

    /// <summary>Columns: class_desc, permission_name, state_desc, securable text. Requires <see cref="PrincipalParameter"/>.</summary>
    public static string ListServerPermissions()
        => $"""
            SELECT p.class_desc, p.permission_name, p.state_desc,
                   CASE WHEN p.class = 100 THEN NULL ELSE CONCAT(p.class_desc, N' #', p.major_id) END
            FROM sys.server_permissions AS p
            INNER JOIN sys.server_principals AS g ON g.principal_id = p.grantee_principal_id
            WHERE g.name = {PrincipalParameter}
            ORDER BY p.class_desc, p.permission_name;
            """;

    /// <summary>Columns: name, type_desc, authentication_type_desc, default_schema_name, mapped login name (NULL when none is visible).</summary>
    public static string ListDatabaseUsers(string database)
    {
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT u.name, u.type_desc, u.authentication_type_desc, u.default_schema_name, l.name
            FROM {db}.sys.database_principals AS u
            LEFT JOIN sys.server_principals AS l ON u.sid IS NOT NULL AND l.sid = u.sid
            WHERE u.type IN ({LoginTypes})
            ORDER BY u.name;
            """;
    }

    /// <summary>Columns: role name, is_fixed_role, member name (NULL for an empty role), member type_desc.</summary>
    public static string ListDatabaseRoleMembers(string database)
    {
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT r.name, CONVERT(bit, r.is_fixed_role), m.name, m.type_desc
            FROM {db}.sys.database_principals AS r
            LEFT JOIN {db}.sys.database_role_members AS rm ON rm.role_principal_id = r.principal_id
            LEFT JOIN {db}.sys.database_principals AS m ON m.principal_id = rm.member_principal_id
            WHERE r.type = 'R'
            ORDER BY r.name, m.name;
            """;
    }

    /// <summary>
    /// Columns: class_desc, permission_name, state_desc, securable text (object/column/schema when resolvable).
    /// Requires <see cref="PrincipalParameter"/>.
    /// </summary>
    public static string ListDatabasePermissions(string database)
    {
        var db = SqlIdentity.QuoteIdentifier(database);
        return $"""
            SELECT p.class_desc, p.permission_name, p.state_desc,
                   CASE p.class
                       WHEN 0 THEN NULL
                       WHEN 1 THEN QUOTENAME(os.name) + N'.' + QUOTENAME(o.name) + CASE WHEN p.minor_id > 0 THEN N' (' + c.name + N')' ELSE N'' END
                       WHEN 3 THEN N'SCHEMA::' + QUOTENAME(sc.name)
                       ELSE CONCAT(p.class_desc, N' #', p.major_id)
                   END
            FROM {db}.sys.database_permissions AS p
            INNER JOIN {db}.sys.database_principals AS g ON g.principal_id = p.grantee_principal_id
            LEFT JOIN {db}.sys.objects AS o ON p.class = 1 AND o.object_id = p.major_id
            LEFT JOIN {db}.sys.schemas AS os ON os.schema_id = o.schema_id
            LEFT JOIN {db}.sys.columns AS c ON p.class = 1 AND p.minor_id > 0 AND c.object_id = p.major_id AND c.column_id = p.minor_id
            LEFT JOIN {db}.sys.schemas AS sc ON p.class = 3 AND sc.schema_id = p.major_id
            WHERE g.name = {PrincipalParameter}
            ORDER BY p.class, p.permission_name;
            """;
    }

    public static IReadOnlyList<ServerLogin> ParseServerLogins(RawRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = new List<ServerLogin>(rows.Rows.Count);
        foreach (var r in rows.Rows)
        {
            if (r.Length < 4)
                continue;
            list.Add(new ServerLogin(Text(r[0]) ?? "", Text(r[1]) ?? "", Flag(r[2]), Text(r[3])));
        }
        return list;
    }

    public static IReadOnlyList<DatabaseUser> ParseDatabaseUsers(RawRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = new List<DatabaseUser>(rows.Rows.Count);
        foreach (var r in rows.Rows)
        {
            if (r.Length < 5)
                continue;
            list.Add(new DatabaseUser(Text(r[0]) ?? "", Text(r[1]) ?? "", Text(r[2]) ?? "", Text(r[3]), Text(r[4])));
        }
        return list;
    }

    public static IReadOnlyList<RoleMembership> ParseRoleMemberships(RawRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = new List<RoleMembership>(rows.Rows.Count);
        foreach (var r in rows.Rows)
        {
            if (r.Length < 4)
                continue;
            list.Add(new RoleMembership(Text(r[0]) ?? "", Flag(r[1]), Text(r[2]), Text(r[3])));
        }
        return list;
    }

    public static IReadOnlyList<GrantedPermission> ParseGrantedPermissions(RawRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = new List<GrantedPermission>(rows.Rows.Count);
        foreach (var r in rows.Rows)
        {
            if (r.Length < 4)
                continue;
            list.Add(new GrantedPermission(Text(r[0]) ?? "", Text(r[1]) ?? "", Text(r[2]) ?? "", Text(r[3])));
        }
        return list;
    }

    /// <summary>Null stays null; an oversized cell the reader refused to load is named, never shown as empty.</summary>
    static string? Text(object? value) => value switch
    {
        null or DBNull => null,
        RawCellOmitted omitted => $"({omitted.Reason})",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    static bool Flag(object? value) => value switch
    {
        bool b => b,
        null or DBNull => false,
        _ => Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0
    };
}

/// <summary>
/// Maps a failed security catalog read to explicit, single-line, redacted text so a denied folder is never shown
/// like an empty one. Reuses <see cref="CatalogFailure"/> so Object Explorer and the security window agree.
/// </summary>
public static class SecurityFailure
{
    // 229/230 object/column, 262 database, 297/300 server-state, 916 cannot access database, 15247 "no permission to perform this action".
    static readonly HashSet<int> DeniedNumbers = [229, 230, 262, 297, 300, 916, 15247];

    public static int NumberOf(Exception ex) => ex switch
    {
        SqlException sql => sql.Number,
        ProviderErrorException provider => provider.Number,
        _ => 0
    };

    public static bool IsDenied(int number) => DeniedNumbers.Contains(number);

    /// <param name="serverScope">True for server-level reads (needs VIEW ANY DEFINITION); false for database-level (VIEW DEFINITION on the database).</param>
    public static CatalogFailure From(Exception ex, string what, bool serverScope)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var number = NumberOf(ex);
        if (ex is not OperationCanceledException && IsDenied(number))
        {
            var label = SchemaObjectCatalog.OneLine(DiagnosticsRedactor.Redact(what));
            var ask = serverScope
                ? "VIEW ANY DEFINITION (or the specific server permission) on the server"
                : "VIEW DEFINITION on the database";
            return new CatalogFailure(CatalogFailureKind.Denied, number,
                $"Denied: this login cannot read {label} (SQL error {number}). Ask an administrator for {ask}.");
        }
        return CatalogFailure.From(ex, what);
    }
}

/// <summary>Reads the security catalog through an <see cref="ISecurityQueryRunner"/>. Never writes and never opens a transaction.</summary>
public sealed class SecurityCatalogReader(ISecurityQueryRunner runner)
{
    readonly ISecurityQueryRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public Task<SecurityListing<ServerLogin>> ReadServerLoginsAsync(CancellationToken ct = default)
        => ReadAsync(SecurityCatalog.ListServerLogins(), [], "server logins", true, SecurityCatalog.ParseServerLogins, ct);

    public Task<SecurityListing<RoleMembership>> ReadServerRoleMembersAsync(CancellationToken ct = default)
        => ReadAsync(SecurityCatalog.ListServerRoleMembers(), [], "server role members", true, SecurityCatalog.ParseRoleMemberships, ct);

    public Task<SecurityListing<GrantedPermission>> ReadServerPermissionsAsync(string principal, CancellationToken ct = default)
        => ReadAsync(SecurityCatalog.ListServerPermissions(), Principal(principal), "server permissions", true, SecurityCatalog.ParseGrantedPermissions, ct);

    public Task<SecurityListing<DatabaseUser>> ReadDatabaseUsersAsync(string database, CancellationToken ct = default)
        => ReadAsync(SecurityCatalog.ListDatabaseUsers(database), [], "database users", false, SecurityCatalog.ParseDatabaseUsers, ct);

    public Task<SecurityListing<RoleMembership>> ReadDatabaseRoleMembersAsync(string database, CancellationToken ct = default)
        => ReadAsync(SecurityCatalog.ListDatabaseRoleMembers(database), [], "database role members", false, SecurityCatalog.ParseRoleMemberships, ct);

    public Task<SecurityListing<GrantedPermission>> ReadDatabasePermissionsAsync(string database, string principal, CancellationToken ct = default)
        => ReadAsync(SecurityCatalog.ListDatabasePermissions(database), Principal(principal), "database permissions", false, SecurityCatalog.ParseGrantedPermissions, ct);

    /// <summary>The login this session is connected as, or null when it cannot be read. Never throws for a server failure.</summary>
    public async Task<string?> ReadCurrentPrincipalAsync(CancellationToken ct = default)
    {
        try
        {
            var rows = await _runner.QueryAsync(SecurityCatalog.CurrentPrincipal(), [], 1, ct).ConfigureAwait(false);
            return rows.Rows.Count > 0 && rows.Rows[0].Length > 0 && rows.Rows[0][0] is string s && s.Length > 0 ? s : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    static IReadOnlyList<EditParameter> Principal(string principal)
    {
        if (string.IsNullOrWhiteSpace(principal))
            throw new ArgumentException("A principal name is required.", nameof(principal));
        return [new EditParameter(SecurityCatalog.PrincipalParameter, principal)];
    }

    async Task<SecurityListing<T>> ReadAsync<T>(
        string sql, IReadOnlyList<EditParameter> parameters, string what, bool serverScope,
        Func<RawRows, IReadOnlyList<T>> parse, CancellationToken ct)
    {
        try
        {
            var rows = await _runner.QueryAsync(sql, parameters, SecurityCatalog.MaxRows, ct).ConfigureAwait(false);
            return new SecurityListing<T>(parse(rows), rows.Truncated, rows.ByteBudgetExhausted, null);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            return SecurityListing<T>.Failed(SecurityFailure.From(ex, what, serverScope));
        }
    }
}
