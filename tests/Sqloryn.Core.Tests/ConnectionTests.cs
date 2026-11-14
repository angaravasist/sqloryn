using Microsoft.Data.SqlClient;
using Sqloryn.Core;

/// <summary>
/// Connections: structured database-name acceptance, capability probe soft-fail, TLS summary,
/// profile group compatibility and ConnectWindow auth-label source scans. Component-only: no SQL Server connection.
/// </summary>
static class ConnectionTests
{
    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"FAIL {name}");
    }

    static void Pass(string name) => Console.WriteLine($"PASS {name}");

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SQLoryn.slnx")))
                return d.FullName;
        throw new Exception("FAIL repo root (SQLoryn.slnx) not found from test base directory");
    }

    public static void Run()
    {
        DatabaseNames();
        Capabilities();
        TlsSummaryText();
        ProfileGroup();
        ConnectWindowScan();
    }

    static ConnectionOptions Options(string database) => new()
    {
        Server = "db.example",
        UserId = "app",
        Password = "Aa1!secret",
        Database = database
    };

    static void DatabaseNames()
    {
        var accepted = new[]
        {
            "master",
            "My[Db]",
            "]weird[",
            "has space",
            "Ünïcødé数据库",
            "semi;colon=eq'quote\"x",
            "{braces}",
            "trailing-dot.",
            new string('d', 128),
            new string('漢', 128)
        };
        foreach (var name in accepted)
        {
            var o = Options(name);
            o.Validate();
            // The structured builder must carry the exact name through a parse of its own output.
            var parsed = new SqlConnectionStringBuilder(o.BuildConnectionString());
            Check(parsed.InitialCatalog == name, $"db-name-roundtrip:{name}");
            // The name must not leak into any other connection-string key.
            Check(parsed.UserID == "app" && parsed.DataSource == "tcp:db.example", $"db-name-no-injection:{name}");
        }
        Pass("db-name-accepts-brackets-unicode-space-and-specials");

        var injected = new SqlConnectionStringBuilder(Options("x;Encrypt=False;Trust Server Certificate=True").BuildConnectionString());
        Check(injected.Encrypt == SqlConnectionEncryptOption.Mandatory && !injected.TrustServerCertificate
              && injected.InitialCatalog == "x;Encrypt=False;Trust Server Certificate=True", "db-name-injection-stays-data");
        Pass("db-name-injection-stays-data");

        var rejected = new[]
        {
            "", " ", "\t", new string('d', 129), "a\0b", "a\nb", "a\rb", "a\u0007b", "a\u007fb"
        };
        foreach (var name in rejected)
        {
            var threw = false;
            try { Options(name).Validate(); }
            catch (ArgumentException) { threw = true; }
            Check(threw, $"db-name-rejects:{name.Length}:{(name.Length > 0 ? ((int)name[0]).ToString() : "empty")}");
        }
        Pass("db-name-documented-exclusions");

        Check(ConnectionOptions.DatabaseNameExclusions.Contains("control", StringComparison.OrdinalIgnoreCase)
              && ConnectionOptions.DatabaseNameExclusions.Contains("128", StringComparison.Ordinal), "db-name-exclusions-documented");
        Pass("db-name-exclusions-documented");
    }

    sealed class ScriptedRunner(Func<string, object?[][]> respond) : ICapabilityQueryRunner
    {
        public List<string> Seen { get; } = [];

        public Task<IReadOnlyList<object?[]>> QueryAsync(string sql, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Seen.Add(sql);
            return Task.FromResult<IReadOnlyList<object?[]>>(respond(sql));
        }
    }

    static object?[][] Row(params object?[] cells) => [cells];

    static bool IsIdentity(string sql) => sql.Contains("SERVERPROPERTY", StringComparison.Ordinal);
    static bool IsShowplan(string sql) => sql.Contains("HAS_PERMS_BY_NAME", StringComparison.Ordinal);
    static bool IsAgent(string sql) => sql.Contains("dm_server_services", StringComparison.Ordinal);

    static void Capabilities()
    {
        var unprobed = ConnectionCapabilities.Unprobed("16.0.4000.1");
        Check(unprobed.ProductVersion == "16.0.4000.1" && !unprobed.Probed
              && unprobed.HasShowplanPermission is null && unprobed.SupportsAgent is null, "caps-unprobed-unknown");
        Pass("caps-unprobed-unknown");

        var full = ConnectionCapabilities.ProbeAsync(new ScriptedRunner(sql =>
            IsIdentity(sql) ? Row("16.0.4000.1", "Developer Edition (64-bit)", 3)
            : IsShowplan(sql) ? Row(1)
            : IsAgent(sql) ? Row(1)
            : throw new Exception("unexpected sql")), null, CancellationToken.None).GetAwaiter().GetResult();
        Check(full is { Probed: true, ProductVersion: "16.0.4000.1", EngineEdition: 3, HasShowplanPermission: true, SupportsAgent: true }
              && full.Edition == "Developer Edition (64-bit)" && full.Notes.Count == 0, "caps-probe-success");
        Pass("caps-probe-success");

        // Permission denial on showplan and agent probes: soft-fail to unknown, identity retained, no throw.
        var denied = ConnectionCapabilities.ProbeAsync(new ScriptedRunner(sql =>
            IsIdentity(sql) ? Row("15.0.2000.5", "Standard Edition (64-bit)", 2)
            : throw new UnauthorizedAccessException("The SHOWPLAN permission was denied")), null, CancellationToken.None).GetAwaiter().GetResult();
        Check(denied.Probed && denied.ProductVersion == "15.0.2000.5" && denied.EngineEdition == 2
              && denied.HasShowplanPermission is null && denied.SupportsAgent is null
              && denied.Notes.Count == 2, "caps-denial-soft-fails-per-probe");
        Check(denied.Notes.All(n => !n.Contains("SHOWPLAN permission was denied", StringComparison.Ordinal)), "caps-notes-omit-raw-server-text");
        Pass("caps-denial-soft-fails-per-probe");

        // Every query fails (even identity): never throws; falls back to the connect-time version.
        var allFail = ConnectionCapabilities.ProbeAsync(new ScriptedRunner(_ => throw new InvalidOperationException("boom")),
            "14.0.1", CancellationToken.None).GetAwaiter().GetResult();
        Check(allFail.ProductVersion == "14.0.1" && allFail.Edition is null && allFail.EngineEdition is null
              && allFail.HasShowplanPermission is null && allFail.SupportsAgent is null && allFail.Probed
              && allFail.Notes.Count >= 1, "caps-all-fail-soft");
        Pass("caps-all-fail-soft");

        // Explicit "no showplan" is false, not unknown; NULL stays unknown.
        var no = ConnectionCapabilities.ProbeAsync(new ScriptedRunner(sql =>
            IsIdentity(sql) ? Row("16.0", "Enterprise", 3) : IsShowplan(sql) ? Row(0) : Row(2)), null, CancellationToken.None).GetAwaiter().GetResult();
        Check(no.HasShowplanPermission == false, "caps-showplan-false");
        var nullPerm = ConnectionCapabilities.ProbeAsync(new ScriptedRunner(sql =>
            IsIdentity(sql) ? Row("16.0", "Enterprise", 3) : IsShowplan(sql) ? Row(DBNull.Value) : Row(1)), null, CancellationToken.None).GetAwaiter().GetResult();
        Check(nullPerm.HasShowplanPermission is null, "caps-showplan-null-unknown");
        Pass("caps-showplan-tristate");

        // Azure SQL Database / Express have no SQL Server Agent: known false without running the agent query.
        var azure = new ScriptedRunner(sql => IsIdentity(sql) ? Row("12.0.2000.8", "SQL Azure", 5) : IsShowplan(sql) ? Row(1) : throw new Exception("agent query must not run"));
        var azureCaps = ConnectionCapabilities.ProbeAsync(azure, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(azureCaps.SupportsAgent == false && !azure.Seen.Any(IsAgent), "caps-azure-sql-no-agent");
        Pass("caps-azure-sql-no-agent");

        // Cancellation is the caller's decision and must propagate, not be swallowed as a soft failure.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = false;
        try { ConnectionCapabilities.ProbeAsync(new ScriptedRunner(_ => Row(1)), null, cts.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "caps-cancellation-propagates");
        Pass("caps-cancellation-propagates");

        foreach (var n in new[] { 229, 262, 297, 300, 371, 916, 15151 })
            Check(ConnectionCapabilities.IsPermissionDeniedNumber(n), $"caps-denied-number-{n}");
        Check(!ConnectionCapabilities.IsPermissionDeniedNumber(18456) && !ConnectionCapabilities.IsPermissionDeniedNumber(-2), "caps-nondenied-number");
        Pass("caps-permission-number-classification");

        // Source scan: probe is opt-in (not part of ConnectAsync's hard path) and lives behind the session gate.
        var session = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Sqloryn.Core", "SqlSession.cs"));
        Check(session.Contains("ProbeCapabilitiesAsync", StringComparison.Ordinal) && session.Contains("ConnectionCapabilities.Unprobed", StringComparison.Ordinal),
            "caps-session-wiring");
        Pass("caps-session-wiring");
    }

    static void TlsSummaryText()
    {
        var strict = TlsSummary.Describe(false, null);
        Check(strict.Contains("encrypt", StringComparison.OrdinalIgnoreCase) && strict.Contains("validated", StringComparison.OrdinalIgnoreCase)
              && !strict.Contains("NOT", StringComparison.Ordinal), "tls-summary-strict");
        var trust = TlsSummary.Describe(true, null);
        Check(trust.Contains("NOT validated", StringComparison.Ordinal) && trust.Contains("lab", StringComparison.OrdinalIgnoreCase), "tls-summary-trust");
        var pin = TlsSummary.Describe(false, "/Users/someone/private/server.crt");
        Check(pin.Contains("server.crt", StringComparison.Ordinal) && !pin.Contains("/Users/someone", StringComparison.Ordinal), "tls-summary-pin-filename-only");
        var both = TlsSummary.Describe(true, "/x/y.crt");
        Check(both.Contains("cannot", StringComparison.OrdinalIgnoreCase), "tls-summary-conflict");
        Pass("tls-summary");
    }

    static void ProfileGroup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqloryn-ws1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConnectionProfileStore(dir);
            var id = Guid.NewGuid();
            store.Upsert(new ConnectionProfile { Id = id, Name = "Dev", Server = "h", Group = "Lab" });
            Check(store.Load().Single().Group == "Lab", "profile-group-roundtrip");

            // Legacy file written before Group existed still loads, with Group null.
            File.WriteAllText(store.FilePath,
                $$"""{"version":1,"profiles":[{"id":"{{id}}","name":"Old","server":"h","port":1433,"database":"master","userId":"u","encrypt":true,"trustServerCertificate":false,"rememberPassword":false}]}""");
            var old = store.Load().Single();
            Check(old.Group is null && old.Name == "Old", "profile-group-legacy-json");

            // A newer file with unknown keys does not break an older shape either.
            File.WriteAllText(store.FilePath,
                $$"""{"version":1,"profiles":[{"id":"{{id}}","name":"New","server":"h","group":"Prod","futureField":1}]}""");
            Check(store.Load().Single().Group == "Prod", "profile-group-unknown-keys-tolerated");

            foreach (var bad in new[] { new string('g', 129), "a\nb" })
            {
                var threw = false;
                try { store.Upsert(new ConnectionProfile { Id = Guid.NewGuid(), Name = "X", Group = bad }); }
                catch (ArgumentException) { threw = true; }
                Check(threw, "profile-group-rejects-invalid");
            }
            Check(!File.ReadAllText(store.FilePath).Contains("password", StringComparison.OrdinalIgnoreCase), "profile-json-secret-free");
            Pass("profile-group");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    static void ConnectWindowScan()
    {
        var appDir = Path.Combine(FindRepoRoot(), "src", "Sqloryn.App");
        var xaml = File.ReadAllText(Path.Combine(appDir, "ConnectWindow.axaml"));
        var doc = System.Xml.Linq.XDocument.Load(Path.Combine(appDir, "ConnectWindow.axaml"));
        var items = doc.Descendants().Where(e => e.Name.LocalName == "ComboBoxItem").ToList();
        System.Xml.Linq.XElement Item(string content) =>
            items.SingleOrDefault(e => e.Attribute("Content")?.Value == content) ?? throw new Exception($"FAIL auth item missing: {content}");
        string? Attr(System.Xml.Linq.XElement e, string local) =>
            e.Attributes().FirstOrDefault(a => a.Name.LocalName == local)?.Value;

        var sql = Item("SQL Server Authentication");
        Check(Attr(sql, "IsEnabled") is null or "True", "auth-sql-enabled");

        var entra = Item("Microsoft Entra ID (unavailable)");
        var integrated = Item("Integrated / Kerberos (unavailable)");
        foreach (var disabled in new[] { entra, integrated })
            Check(Attr(disabled, "IsEnabled") == "False" && !string.IsNullOrWhiteSpace(Attr(disabled, "ToolTip.Tip")), "auth-disabled-with-tooltip");
        Check(Attr(entra, "ToolTip.Tip")!.Contains("Azure.Identity", StringComparison.Ordinal) && Attr(entra, "ToolTip.Tip")!.Contains("Microsoft.Data.SqlClient", StringComparison.Ordinal),
            "auth-entra-tooltip-names-dependency");
        Check(Attr(integrated, "ToolTip.Tip")!.Contains("Kerberos", StringComparison.Ordinal) && Attr(integrated, "ToolTip.Tip")!.Contains("kinit", StringComparison.Ordinal),
            "auth-integrated-tooltip-names-dependency");
        Check(items.All(i => !string.IsNullOrEmpty(i.Attribute("Content")?.Value)), "auth-items-labelled");
        Check(xaml.Contains("Name=\"AuthBox\"", StringComparison.Ordinal) && xaml.Contains("AutomationProperties.Name=\"Authentication method\"", StringComparison.Ordinal),
            "auth-combobox-accessible");

        // No fake enable: code-behind must not enable the disabled items or branch on an Entra/integrated connect path.
        var code = File.ReadAllText(Path.Combine(appDir, "ConnectWindow.axaml.cs"));
        Check(!code.Contains("Authentication = ", StringComparison.Ordinal) && !code.Contains("ActiveDirectory", StringComparison.Ordinal)
              && !code.Contains("IntegratedSecurity", StringComparison.Ordinal), "auth-no-fake-enable");
        var core = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Sqloryn.Core", "ConnectionOptions.cs"));
        Check(!core.Contains("SqlAuthenticationMethod", StringComparison.Ordinal) && !core.Contains("IntegratedSecurity", StringComparison.Ordinal),
            "auth-core-no-entra-path");

        Check(xaml.Contains("Name=\"TlsSummaryText\"", StringComparison.Ordinal) && code.Contains("TlsSummary.Describe", StringComparison.Ordinal),
            "connect-window-tls-summary");
        Check(xaml.Contains("Name=\"GroupBox\"", StringComparison.Ordinal) && code.Contains("Group = ", StringComparison.Ordinal), "connect-window-group");
        var main = File.ReadAllText(Path.Combine(appDir, "MainWindow.axaml.cs"));
        Check(main.Contains("Group = dialog.ProfileGroup", StringComparison.Ordinal), "main-persists-group");
        Pass("connect-window-auth-and-tls-scan");
    }
}
