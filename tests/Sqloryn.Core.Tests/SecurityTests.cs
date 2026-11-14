using Sqloryn.Core;

/// <summary>
/// Security administration: bounded read-only catalog queries, denial mapping, GRANT/DENY/REVOKE preview and the
/// transactional apply (against a fake). Component-only: no SQL Server connection. Role-change behaviour from a separate
/// principal, partial failure on a real server and server versus database scope on a live target are NOT claimed here.
/// </summary>
static class SecurityTests
{
    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"FAIL {name}");
    }

    static void Pass(string name) => Console.WriteLine($"PASS {name}");

    static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Pass(name); return; }
        throw new Exception($"FAIL {name}");
    }

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SQLoryn.slnx")))
                return d.FullName;
        throw new Exception("FAIL repo root (SQLoryn.slnx) not found from test base directory");
    }

    static string Source(params string[] parts) => File.ReadAllText(Path.Combine([FindRepoRoot(), .. parts]));

    public static async Task RunAsync()
    {
        CatalogSql();
        Parsing();
        await ReaderAsync();
        PreviewSql();
        PreviewValidation();
        PreviewWarnings();
        await PreviewLeavesPermissionsUnchangedAsync();
        await ApplyAsync();
        AppWiring();
    }

    static PermissionChange Obj(PermissionAction action, string permission = "SELECT", string principal = "app_reader",
        string db = "Sales", string schema = "dbo", string name = "Orders")
        => new(action, PermissionScope.Object, permission, principal, db, schema, name);

    // ---- catalog queries ---------------------------------------------------------------------

    static void CatalogSql()
    {
        var logins = SecurityCatalog.ListServerLogins();
        Check(logins.Contains("sys.server_principals", StringComparison.Ordinal), "sec-logins-source");
        Check(!logins.Contains("password_hash", StringComparison.OrdinalIgnoreCase) &&
              !logins.Contains("sql_logins", StringComparison.OrdinalIgnoreCase) &&
              !logins.Contains("sys.sql_logins", StringComparison.OrdinalIgnoreCase), "sec-logins-never-reads-password-hash");
        Check(logins.Contains("ORDER BY", StringComparison.Ordinal) && !logins.Contains("INSERT", StringComparison.OrdinalIgnoreCase), "sec-logins-ordered-read-only");
        Check(logins.Contains("'R'", StringComparison.Ordinal) == false, "sec-logins-excludes-roles-by-type-list");

        var users = SecurityCatalog.ListDatabaseUsers("We]ird");
        Check(users.Contains("[We]]ird].sys.database_principals", StringComparison.Ordinal), "sec-users-quotes-database");
        Check(SecurityCatalog.ListDatabaseRoleMembers("We]ird").Contains("[We]]ird].sys.database_role_members", StringComparison.Ordinal), "sec-roles-quotes-database");
        Check(SecurityCatalog.ListDatabasePermissions("We]ird").Contains("[We]]ird].sys.database_permissions", StringComparison.Ordinal), "sec-dbperms-quotes-database");
        Throws<ArgumentException>(() => SecurityCatalog.ListDatabaseUsers(""), "sec-users-empty-db-rejected");

        // Principal names are bound, never inlined.
        var perms = SecurityCatalog.ListDatabasePermissions("Sales");
        Check(perms.Contains(SecurityCatalog.PrincipalParameter, StringComparison.Ordinal), "sec-dbperms-parameterised");
        Check(SecurityCatalog.ListServerPermissions().Contains(SecurityCatalog.PrincipalParameter, StringComparison.Ordinal), "sec-serverperms-parameterised");
        Check(SecurityCatalog.ListServerRoleMembers().Contains("sys.server_role_members", StringComparison.Ordinal), "sec-server-roles-source");
        Pass("security-catalog-sql");
    }

    static void Parsing()
    {
        Check(SecurityCatalog.ParseServerLogins(FakeSecurityRunner.Rows()).Count == 0, "sec-parse-logins-empty");
        var logins = SecurityCatalog.ParseServerLogins(FakeSecurityRunner.Rows(false, false,
            ["app", "SQL_LOGIN", false, "Sales"],
            ["off", "SQL_LOGIN", true, DBNull.Value],
            ["short"],
            ["DOM\\grp", "WINDOWS_GROUP", 0, "master"]));
        Check(logins.Count == 3, "sec-parse-logins-skips-malformed");
        Check(logins[0] is { Name: "app", IsDisabled: false, DefaultDatabase: "Sales" }, "sec-parse-login-fields");
        Check(logins[1] is { IsDisabled: true, DefaultDatabase: null }, "sec-parse-login-disabled-null-default");
        Check(logins[2].TypeDescription == "WINDOWS_GROUP", "sec-parse-login-type-passthrough");

        var users = SecurityCatalog.ParseDatabaseUsers(FakeSecurityRunner.Rows(false, false,
            ["mapped", "SQL_USER", "INSTANCE", "dbo", "app"],
            ["orphan", "SQL_USER", "INSTANCE", "dbo", DBNull.Value],
            ["contained", "SQL_USER", "DATABASE", "dbo", DBNull.Value],
            ["guest", "SQL_USER", "NONE", "guest", DBNull.Value]));
        Check(users.Count == 4, "sec-parse-users-count");
        Check(!users[0].HasNoVisibleLogin && users[0].MappedLogin == "app", "sec-parse-user-mapped");
        Check(users[1].HasNoVisibleLogin && !users[1].IsContained, "sec-parse-user-orphan-candidate");
        Check(users[2].IsContained && !users[2].HasNoVisibleLogin, "sec-parse-user-contained-not-orphan");
        Check(!users[3].HasNoVisibleLogin, "sec-parse-user-guest-not-orphan");

        var roles = SecurityCatalog.ParseRoleMemberships(FakeSecurityRunner.Rows(false, false,
            ["db_owner", true, "alice", "SQL_USER"],
            ["empty_role", false, DBNull.Value, DBNull.Value]));
        Check(roles.Count == 2 && roles[0].Member == "alice" && roles[0].IsFixedRole, "sec-parse-role-member");
        Check(roles[1].Member is null && roles[1].MemberType is null, "sec-parse-role-without-members-is-kept");

        var granted = SecurityCatalog.ParseGrantedPermissions(FakeSecurityRunner.Rows(false, false,
            ["DATABASE", "CONNECT", "GRANT", DBNull.Value],
            ["OBJECT_OR_COLUMN", "SELECT", "DENY", "[dbo].[Orders]"]));
        Check(granted.Count == 2 && granted[1] is { Permission: "SELECT", State: "DENY", Securable: "[dbo].[Orders]" }, "sec-parse-permissions");
        Pass("security-catalog-parse");
    }

    // ---- reader: empty lists, truncation and denial mapping ------------------------------------

    static async Task ReaderAsync()
    {
        var empty = new FakeSecurityRunner();
        var reader = new SecurityCatalogReader(empty);
        var none = await reader.ReadServerLoginsAsync();
        Check(none.Failure is null && none.Items.Count == 0 && none.Note is null, "sec-reader-empty-is-not-an-error");
        Check(empty.Calls[0].MaxRows == SecurityCatalog.MaxRows && empty.Calls[0].MaxRows > 0, "sec-reader-bounded");

        var truncated = new SecurityCatalogReader(new FakeSecurityRunner(_ => FakeSecurityRunner.Rows(true, false, ["a", "SQL_LOGIN", false, DBNull.Value])));
        var t = await truncated.ReadServerLoginsAsync();
        Check(t.Truncated && t.Items.Count == 1 && t.Note is not null && t.Note.Contains("truncated", StringComparison.OrdinalIgnoreCase), "sec-reader-truncation-is-named");

        var principalRunner = new FakeSecurityRunner();
        await new SecurityCatalogReader(principalRunner).ReadDatabasePermissionsAsync("Sales", "O'Brien");
        var p = principalRunner.Calls[0];
        Check(p.Parameters.Count == 1 && p.Parameters[0].Name == "@principal" && (string?)p.Parameters[0].Value == "O'Brien" &&
              !p.Sql.Contains("Brien", StringComparison.Ordinal), "sec-reader-binds-principal");

        // denial mapping: honest, single line, not an empty list
        var denied = await new SecurityCatalogReader(new FakeSecurityRunner(fail: new ProviderErrorException(297, "The user does not have permission\nto perform this action.")))
            .ReadServerLoginsAsync();
        Check(denied.Failure is { Kind: CatalogFailureKind.Denied, Number: 297 } && denied.Items.Count == 0, "sec-denied-297");
        Check(denied.Note is not null && denied.Note.StartsWith("Denied:", StringComparison.Ordinal) && !denied.Note.Contains('\n') &&
              denied.Note.Contains("VIEW ANY DEFINITION", StringComparison.Ordinal), "sec-denied-text-names-permission");
        var denied229 = await new SecurityCatalogReader(new FakeSecurityRunner(fail: new ProviderErrorException(229, "x"))).ReadDatabaseUsersAsync("Sales");
        Check(denied229.Failure?.Kind == CatalogFailureKind.Denied && denied229.Note!.Contains("VIEW DEFINITION", StringComparison.Ordinal), "sec-denied-database-users");
        var denied916 = await new SecurityCatalogReader(new FakeSecurityRunner(fail: new ProviderErrorException(916, "x"))).ReadDatabaseRoleMembersAsync("Sales");
        Check(denied916.Failure?.Kind == CatalogFailureKind.Denied, "sec-denied-916-database-roles");

        var error = await new SecurityCatalogReader(new FakeSecurityRunner(fail: new ProviderErrorException(208, "Invalid object name. Password=Hunter2;")))
            .ReadServerRoleMembersAsync();
        Check(error.Failure is { Kind: CatalogFailureKind.Error, Number: 208 } && !error.Note!.Contains("Hunter2", StringComparison.Ordinal) &&
              !error.Note.StartsWith("Denied", StringComparison.Ordinal), "sec-error-redacted-and-not-denied");
        var cancelled = await new SecurityCatalogReader(new FakeSecurityRunner(fail: new OperationCanceledException())).ReadServerLoginsAsync();
        Check(cancelled.Failure?.Kind == CatalogFailureKind.Cancelled, "sec-cancelled-mapped");
        Pass("security-reader");
    }

    // ---- preview SQL ---------------------------------------------------------------------------

    static void PreviewSql()
    {
        var grant = PermissionChangePreview.Create([Obj(PermissionAction.Grant)]);
        Check(grant.Statements.Count == 1 && grant.HasChanges, "sec-preview-one-statement");
        Check(grant.Statements[0].Sql == "EXEC [Sales].sys.sp_executesql N'GRANT SELECT ON OBJECT::[dbo].[Orders] TO [app_reader];';", "sec-preview-grant-object-database-targeted");
        Check(grant.ExecutableSql == grant.Statements[0].Sql, "sec-preview-executable-is-statements");

        var server = PermissionChangePreview.Create([new PermissionChange(PermissionAction.Grant, PermissionScope.Server, "VIEW SERVER STATE", "report_login")]);
        Check(server.Statements[0].Sql == "GRANT VIEW SERVER STATE TO [report_login];", "sec-preview-server-scope-no-database-wrapper");

        var db = PermissionChangePreview.Create([new PermissionChange(PermissionAction.Grant, PermissionScope.Database, "CONNECT", "u", Database: "Sales")]);
        Check(db.Statements[0].Sql == "EXEC [Sales].sys.sp_executesql N'GRANT CONNECT TO [u];';", "sec-preview-database-scope");

        var schema = PermissionChangePreview.Create([new PermissionChange(PermissionAction.Deny, PermissionScope.Schema, "SELECT", "u", "Sales", "hr", Cascade: true)]);
        Check(schema.Statements[0].Sql == "EXEC [Sales].sys.sp_executesql N'DENY SELECT ON SCHEMA::[hr] TO [u] CASCADE;';", "sec-preview-deny-schema-cascade");

        var revoke = PermissionChangePreview.Create([Obj(PermissionAction.Revoke) with { WithGrantOption = true, Cascade = true }]);
        Check(revoke.Statements[0].Sql == "EXEC [Sales].sys.sp_executesql N'REVOKE GRANT OPTION FOR SELECT ON OBJECT::[dbo].[Orders] FROM [app_reader] CASCADE;';", "sec-preview-revoke-grant-option-cascade");
        var plainRevoke = PermissionChangePreview.Create([Obj(PermissionAction.Revoke, "UPDATE")]);
        Check(plainRevoke.Statements[0].Sql.Contains("REVOKE UPDATE ON OBJECT::[dbo].[Orders] FROM [app_reader];", StringComparison.Ordinal), "sec-preview-revoke-uses-FROM");

        var withGrant = PermissionChangePreview.Create([Obj(PermissionAction.Grant) with { WithGrantOption = true }]);
        Check(withGrant.Statements[0].Sql.Contains("TO [app_reader] WITH GRANT OPTION;", StringComparison.Ordinal), "sec-preview-with-grant-option");

        var columns = PermissionChangePreview.Create([Obj(PermissionAction.Grant) with { Columns = ["Id", "Na]me"] }]);
        Check(columns.Statements[0].Sql.Contains("GRANT SELECT ([Id], [Na]]me]) ON OBJECT::", StringComparison.Ordinal), "sec-preview-column-list-quoted");

        // Quoting: ] doubles inside brackets, ' doubles again inside the N'' literal that carries the statement.
        var hostile = PermissionChangePreview.Create([Obj(PermissionAction.Grant, principal: "O'Brien]", db: "d'b]", schema: "s'", name: "t]x")]);
        var sql = hostile.Statements[0].Sql;
        Check(sql.StartsWith("EXEC [d'b]]].sys.sp_executesql N'", StringComparison.Ordinal), "sec-preview-database-name-quoted-outside-literal");
        Check(sql.Contains("TO [O''Brien]]]", StringComparison.Ordinal) && sql.Contains("[s'']", StringComparison.Ordinal) && sql.Contains("[t]]x]", StringComparison.Ordinal),
            "sec-preview-quotes-and-brackets-escaped-in-literal");
        // The literal is closed exactly once: stripping every escaped quote leaves one opening and one closing quote.
        var literal = sql[(sql.IndexOf("N'", StringComparison.Ordinal) + 1)..^1];
        Check(literal.StartsWith('\'') && literal.EndsWith('\'') && literal[1..^1].Replace("''", "").IndexOf('\'') < 0, "sec-preview-literal-has-no-unescaped-quote");

        var two = PermissionChangePreview.Create([Obj(PermissionAction.Grant), Obj(PermissionAction.Deny, "DELETE")]);
        Check(two.Statements.Count == 2 && two.ExecutableSql.Split('\n').Length == 2, "sec-preview-multiple-statements-one-per-line");
        Check(PermissionChangePreview.Create([Obj(PermissionAction.Grant)]).Statements[0].Description.Contains('\n') == false, "sec-preview-description-single-line");
        Pass("security-preview-sql");
    }

    static void PreviewValidation()
    {
        Throws<ArgumentException>(() => PermissionChangePreview.Create([]), "sec-preview-empty-rejected");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([Obj(PermissionAction.Grant, "SELECT; DROP LOGIN sa")]), "sec-preview-permission-injection-rejected");
        Check(PermissionChangePreview.Create([Obj(PermissionAction.Grant, " select ")]).Statements[0].Sql.Contains("GRANT SELECT ON", StringComparison.Ordinal), "sec-preview-permission-canonicalised-from-allowlist");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([Obj(PermissionAction.Grant, "NOT A PERMISSION")]), "sec-preview-unknown-permission-rejected");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([new PermissionChange(PermissionAction.Grant, PermissionScope.Server, "SELECT", "u")]), "sec-preview-permission-scope-mismatch-rejected");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([Obj(PermissionAction.Grant, principal: "")]), "sec-preview-empty-principal-rejected");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([Obj(PermissionAction.Grant, principal: "a\nGO\nb")]), "sec-preview-newline-in-name-rejected");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([Obj(PermissionAction.Grant, principal: "a\0b")]), "sec-preview-nul-in-name-rejected");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([Obj(PermissionAction.Grant, name: new string('x', 129))]), "sec-preview-overlong-name-rejected");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([Obj(PermissionAction.Grant, db: "")]), "sec-preview-missing-database-rejected");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([new PermissionChange(PermissionAction.Grant, PermissionScope.Object, "SELECT", "u", "Sales", "dbo", null)]), "sec-preview-missing-object-rejected");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([Obj(PermissionAction.Deny) with { WithGrantOption = true }]), "sec-preview-deny-with-grant-option-rejected");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([Obj(PermissionAction.Grant) with { Cascade = true }]), "sec-preview-grant-cascade-rejected");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([Obj(PermissionAction.Grant, "DELETE") with { Columns = ["a"] }]), "sec-preview-columns-only-for-select-update-references");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([new PermissionChange(PermissionAction.Grant, PermissionScope.Schema, "SELECT", "u", "Sales", "dbo", Columns: ["a"])]), "sec-preview-columns-only-for-objects");
        Throws<ArgumentException>(() => PermissionChangePreview.Create([Obj(PermissionAction.Grant) with { Columns = [] }]), "sec-preview-empty-column-list-rejected");
        Check(PermissionChangePreview.AllowedPermissions(PermissionScope.Server).Contains("VIEW SERVER STATE") &&
              !PermissionChangePreview.AllowedPermissions(PermissionScope.Object).Contains("VIEW SERVER STATE"), "sec-allowed-permissions-per-scope");
    }

    static void PreviewWarnings()
    {
        var elevated = PermissionChangePreview.Create([Obj(PermissionAction.Grant, "CONTROL")]);
        Check(elevated.Warnings.Any(w => w.Contains("elevated", StringComparison.OrdinalIgnoreCase)), "sec-warn-elevated");
        var deny = PermissionChangePreview.Create([Obj(PermissionAction.Deny)]);
        Check(deny.Warnings.Any(w => w.Contains("overrides", StringComparison.OrdinalIgnoreCase)), "sec-warn-deny-overrides-grants");
        var self = PermissionChangePreview.Create(
            [new PermissionChange(PermissionAction.Deny, PermissionScope.Server, "CONNECT SQL", "Me")], currentPrincipal: "me");
        Check(self.Warnings.Any(w => w.Contains("connected as", StringComparison.OrdinalIgnoreCase)), "sec-warn-self-lockout-case-insensitive");
        var benign = PermissionChangePreview.Create([Obj(PermissionAction.Grant)], "someone_else");
        Check(benign.Warnings.Count == 0, "sec-no-warning-for-plain-grant");
        var revoke = PermissionChangePreview.Create([Obj(PermissionAction.Revoke)]);
        Check(revoke.Warnings.Any(w => w.Contains("role", StringComparison.OrdinalIgnoreCase)), "sec-warn-revoke-does-not-remove-role-permissions");

        // No secret material can be modelled or emitted.
        var names = typeof(PermissionChange).GetProperties().Concat(typeof(PermissionChangePreview).GetProperties())
            .Concat(typeof(PermissionStatement).GetProperties()).Concat(typeof(ServerLogin).GetProperties())
            .Concat(typeof(DatabaseUser).GetProperties()).Select(x => x.Name).ToList();
        Check(!names.Any(n => n.Contains("password", StringComparison.OrdinalIgnoreCase) || n.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                              n.Contains("hash", StringComparison.OrdinalIgnoreCase)), "sec-models-have-no-secret-members");
        var all = string.Join('\n', PermissionChangePreview.Create([Obj(PermissionAction.Grant), Obj(PermissionAction.Deny), Obj(PermissionAction.Revoke)]).Statements.Select(s => s.Sql + s.Description));
        Check(!all.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) && !all.Contains("CREATE LOGIN", StringComparison.OrdinalIgnoreCase) && !all.Contains("ALTER LOGIN", StringComparison.OrdinalIgnoreCase), "sec-preview-never-emits-credentials");
        Pass("security-preview-warnings");
    }

    static async Task PreviewLeavesPermissionsUnchangedAsync()
    {
        var factory = new FakePermissionTransactionFactory();
        var runner = new FakeSecurityRunner();
        _ = PermissionChangePreview.Create([Obj(PermissionAction.Grant), Obj(PermissionAction.Deny, "DELETE")]);
        Check(factory.Log.Count == 0 && factory.Durable.Count == 0 && runner.Calls.Count == 0, "sec-preview-touches-nothing");
        var a = PermissionChangePreview.Create([Obj(PermissionAction.Grant)]);
        var b = PermissionChangePreview.Create([Obj(PermissionAction.Grant)]);
        Check(a.ExecutableSql == b.ExecutableSql, "sec-preview-deterministic");
        await Task.CompletedTask;
        Pass("security-preview-leaves-permissions-unchanged");
    }

    // ---- apply (fake transaction) ----------------------------------------------------------------

    static async Task ApplyAsync()
    {
        var two = PermissionChangePreview.Create([Obj(PermissionAction.Grant), Obj(PermissionAction.Deny, "DELETE")]);

        var ok = new FakePermissionTransactionFactory();
        var committed = await PermissionChangeApply.ApplyAsync(ok, two);
        Check(committed.Committed && committed.StatementsExecuted == 2 && ok.Durable.Count == 2 && ok.Log.Count(l => l == "commit") == 1, "sec-apply-commits-all");
        Check(ok.Executed.SequenceEqual(two.Statements.Select(s => s.Sql)), "sec-apply-runs-exactly-previewed-sql");

        // partial failure: statement 2 fails, statement 1 must not survive
        var partial = new FakePermissionTransactionFactory(null, new ProviderErrorException(15151, "Cannot find the principal 'x', because it does not exist or you do not have permission."));
        var r = await PermissionChangeApply.ApplyAsync(partial, two);
        Check(r.Outcome == PermissionApplyOutcome.RolledBackError && r.FailedIndex == 1 && r.StatementsExecuted == 1, "sec-apply-partial-failure-reported");
        Check(partial.Durable.Count == 0 && partial.Log.Contains("rollback") && !partial.Log.Contains("commit"), "sec-apply-fake-rollback-leaves-nothing");
        Check(r.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase) && r.Message.Contains("unchanged", StringComparison.OrdinalIgnoreCase), "sec-apply-partial-says-unchanged");

        // refusal by the server when this login may not change permissions
        var refused = new FakePermissionTransactionFactory(new ProviderErrorException(15247, "User does not have permission to perform this action."));
        var d = await PermissionChangeApply.ApplyAsync(refused, two);
        Check(d.Outcome == PermissionApplyOutcome.RefusedDenied && d.FailedIndex == 0 && refused.Durable.Count == 0 && refused.Log.Contains("rollback"), "sec-apply-denied-refused-and-rolled-back");
        Check(d.Message.Contains("not allowed", StringComparison.OrdinalIgnoreCase) || d.Message.Contains("permission", StringComparison.OrdinalIgnoreCase), "sec-apply-denied-message");

        var password = new FakePermissionTransactionFactory(new ProviderErrorException(102, "bad Password=Hunter2; near"));
        var pw = await PermissionChangeApply.ApplyAsync(password, two);
        Check(!pw.Message.Contains("Hunter2", StringComparison.Ordinal), "sec-apply-message-redacts-secrets");

        var begin = new FakePermissionTransactionFactory { BeginFails = new InvalidOperationException("no tx") };
        var b = await PermissionChangeApply.ApplyAsync(begin, two);
        Check(b.Outcome == PermissionApplyOutcome.RolledBackError && b.StatementsExecuted == 0 && begin.Executed.Count == 0, "sec-apply-begin-failure-runs-nothing");

        // commit failure: never claim rollback
        var commit = new FakePermissionTransactionFactory { CommitFails = new IOException("link down") };
        var c = await PermissionChangeApply.ApplyAsync(commit, two);
        Check(c.Outcome == PermissionApplyOutcome.OutcomeUnknown && c.RequiresReload && !c.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase) && !commit.Log.Contains("rollback"), "sec-apply-commit-unknown-never-claims-rollback");

        var rollback = new FakePermissionTransactionFactory(new ProviderErrorException(102, "x")) { RollbackFails = new IOException("down") };
        var rb = await PermissionChangeApply.ApplyAsync(rollback, two);
        Check(rb.Outcome == PermissionApplyOutcome.OutcomeUnknown, "sec-apply-rollback-failure-unknown");

        var dispose = new FakePermissionTransactionFactory { DisposeFails = new IOException("down") };
        var dp = await PermissionChangeApply.ApplyAsync(dispose, two);
        Check(dp.Outcome == PermissionApplyOutcome.OutcomeUnknown, "sec-apply-dispose-failure-after-commit-unknown");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new FakePermissionTransactionFactory();
        var cr = await PermissionChangeApply.ApplyAsync(cancelled, two, cts.Token);
        Check(!cr.Committed && cancelled.Durable.Count == 0 && cancelled.Log.Contains("rollback"), "sec-apply-cancel-rolls-back");

        Throws<ArgumentNullException>(() => PermissionChangeApply.ApplyAsync(ok, null!).GetAwaiter().GetResult(), "sec-apply-null-preview-rejected");
        Pass("security-apply");
    }

    // ---- App wiring (source scans) ----------------------------------------------------------------

    static void AppWiring()
    {
        var window = Source("src", "Sqloryn.App", "SecurityWindow.axaml.cs");
        var xaml = Source("src", "Sqloryn.App", "SecurityWindow.axaml");
        var shell = Source("src", "Sqloryn.App", "MainWindow.Shell.cs");
        var main = Source("src", "Sqloryn.App", "MainWindow.axaml");
        var mainCode = Source("src", "Sqloryn.App", "MainWindow.axaml.cs");
        var session = Source("src", "Sqloryn.Core", "SqlSession.cs");

        var confirm = window.IndexOf("Dialogs.ConfirmAsync", StringComparison.Ordinal);
        var apply = window.IndexOf("ApplyPermissionChangeAsync", StringComparison.Ordinal);
        Check(confirm >= 0 && apply > confirm, "sec-window-confirms-before-apply");
        Check(window.Contains("if (!confirmed)", StringComparison.Ordinal) && window.IndexOf("if (!confirmed)", StringComparison.Ordinal) < apply, "sec-window-returns-when-not-confirmed");
        Check(window.Split("ApplyPermissionChangeAsync").Length == 2, "sec-window-single-apply-call-site");
        Check(window.Contains("PermissionChangePreview.Create", StringComparison.Ordinal), "sec-window-sql-from-core");
        foreach (var verb in new[] { "\"GRANT ", "\"DENY ", "\"REVOKE ", "$\"GRANT", "$\"DENY", "$\"REVOKE" })
            Check(!window.Contains(verb, StringComparison.Ordinal), "sec-window-no-raw-permission-sql " + verb);
        Check(!window.Contains("sys.server_principals", StringComparison.Ordinal) && !window.Contains("_session.ExecuteAsync", StringComparison.Ordinal), "sec-window-no-raw-sql-or-session-execute");
        Check(window.Contains("_previewedSql", StringComparison.Ordinal) && window.Contains("ExecutableSql", StringComparison.Ordinal), "sec-window-applies-only-previewed-sql");
        Check(!xaml.Contains("PasswordChar", StringComparison.Ordinal) && !xaml.Contains("Password", StringComparison.OrdinalIgnoreCase) &&
              !window.Contains("Password", StringComparison.OrdinalIgnoreCase), "sec-window-has-no-password-input");
        Check(xaml.Contains("AutomationProperties.Name", StringComparison.Ordinal) && xaml.Contains("Confirm Apply…", StringComparison.Ordinal), "sec-window-accessible-confirm-label");

        Check(shell.Contains("OnSecurity", StringComparison.Ordinal) && shell.Contains("S_ecurity…", StringComparison.Ordinal), "sec-tools-menu-entry");
        Check(main.Contains("OnSecurity", StringComparison.Ordinal), "sec-explorer-context-menu-entry");
        Check(mainCode.Contains("new SecurityWindow(", StringComparison.Ordinal) && mainCode.Contains("HasActiveCommand", StringComparison.Ordinal), "sec-opens-with-session-busy-guard");

        var applyMethod = session[session.IndexOf("ApplyPermissionChangeAsync", StringComparison.Ordinal)..];
        Check(applyMethod.Contains("XACT_STATE", StringComparison.Ordinal) && applyMethod.Contains("PermissionChangeApply.ApplyAsync", StringComparison.Ordinal) &&
              applyMethod.Contains("DisposeConnectionAsync", StringComparison.Ordinal), "sec-session-owned-transaction-and-unknown-disconnect");
        Pass("security-app-wiring");
    }
}
