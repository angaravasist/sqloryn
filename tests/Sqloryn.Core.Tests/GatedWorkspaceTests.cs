using Sqloryn.Core;

/// <summary>
/// Honest gated shells for high availability, replication and BI/cloud. Component-only: no SQL Server, no cloud call.
/// Asserts that the gate model names the missing fixture/protocol/auth, that no text claims a working or healthy
/// state, and (source scans) that the App views stay disabled, credential-free, network-free and reachable from Tools.
/// </summary>
static class GatedWorkspaceTests
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

    // Affirmative status words an incomplete shell must never show. Gate text says "not verified", never these.
    static readonly string[] ForbiddenClaims = ["healthy", "connected", "synchronized", "synchronised", "online", "succeeded", "success", "ready"];

    static IEnumerable<string> AllText(GatedWorkspace w)
    {
        yield return w.Title;
        yield return w.Summary;
        yield return w.StatusLine;
        foreach (var s in w.Sections)
        {
            yield return s.Title;
            yield return s.StatusText;
            yield return s.Explanation;
            yield return s.MissingRequirement;
        }
    }

    public static void Run()
    {
        Catalog();
        HaAndReplication();
        BiCloud();
        NoPretendClaims();
        ViewSourceScans();
        MenuAndDocsScans();
    }

    static void Catalog()
    {
        var ids = GatedWorkspaceCatalog.All.Select(w => w.Id).ToArray();
        Check(ids.SequenceEqual(["HighAvailabilityWorkspace", "ReplicationWorkspace", "BiCloudWorkspace"]), "gated-catalog-ids");
        Check(ReferenceEquals(GatedWorkspaceCatalog.HighAvailability, GatedWorkspaceCatalog.All[0]), "gated-catalog-ha-accessor");
        foreach (var w in GatedWorkspaceCatalog.All)
        {
            Check(w.Sections.Count > 0, $"gated-{w.Id}-has-sections");
            Check(w.Sections.Select(s => s.Id).Distinct().Count() == w.Sections.Count, $"gated-{w.Id}-unique-section-ids");
            foreach (var s in w.Sections)
            {
                Check(s.State is GateState.Incomplete or GateState.Disabled, $"gated-{w.Id}-{s.Id}-state-not-available");
                Check(!s.ActionEnabled, $"gated-{w.Id}-{s.Id}-action-disabled");
                Check(s.MissingRequirement.Length >= 20, $"gated-{w.Id}-{s.Id}-names-missing-requirement");
                Check(s.CapIds.Count > 0 && s.CapIds.All(c => c.StartsWith("GATE-", StringComparison.Ordinal)), $"gated-{w.Id}-{s.Id}-caps");
                Check(s.StatusText.Contains("Not verified", StringComparison.Ordinal) || s.StatusText.Contains("Disabled", StringComparison.Ordinal), $"gated-{w.Id}-{s.Id}-status-honest");
            }
        }
        Pass("gated-catalog");
    }

    static void HaAndReplication()
    {
        var ha = GatedWorkspaceCatalog.HighAvailability;
        Check(ha.StatusLine.Contains("incomplete", StringComparison.OrdinalIgnoreCase), "gated-ha-incomplete-line");
        Check(ha.Summary.Contains("multi-node", StringComparison.OrdinalIgnoreCase), "gated-ha-names-multi-node-fixture");
        Check(ha.Sections.All(s => s.State == GateState.Incomplete), "gated-ha-sections-incomplete");
        Check(ha.Sections.Any(s => s.CapIds.Contains("GATE-158")), "gated-ha-cap-158");
        Check(ha.Sections.Any(s => s.Title.Contains("Availability Group", StringComparison.OrdinalIgnoreCase)), "gated-ha-availability-groups");
        Check(ha.Sections.Any(s => s.Title.Contains("Log shipping", StringComparison.OrdinalIgnoreCase)), "gated-ha-log-shipping");
        Check(ha.Sections.All(s => s.MissingRequirement.Contains("node", StringComparison.OrdinalIgnoreCase)), "gated-ha-each-section-names-nodes");
        Check(ha.Sections.All(s => s.Explanation.Contains("No health", StringComparison.Ordinal)), "gated-ha-no-health-statement");

        var rep = GatedWorkspaceCatalog.Replication;
        Check(rep.StatusLine.Contains("incomplete", StringComparison.OrdinalIgnoreCase), "gated-repl-incomplete-line");
        Check(rep.Summary.Contains("multi-node", StringComparison.OrdinalIgnoreCase), "gated-repl-names-multi-node-fixture");
        Check(rep.Sections.Any(s => s.CapIds.Contains("GATE-157")), "gated-repl-cap-157");
        foreach (var part in new[] { "Publisher", "Distributor", "Subscriber", "agent", "conflict" })
            Check(rep.Sections.Any(s => s.Title.Contains(part, StringComparison.OrdinalIgnoreCase)), $"gated-repl-section-{part}");
        Check(rep.Sections.All(s => s.State == GateState.Incomplete), "gated-repl-sections-incomplete");
        Pass("gated-ha-replication");
    }

    static void BiCloud()
    {
        var bi = GatedWorkspaceCatalog.BiCloud;
        var expected = new Dictionary<string, string[]>
        {
            ["ssisdb"] = ["SSISDB", "GATE-163", "GATE-164"],
            ["ssas"] = ["SSAS", "GATE-165", "GATE-166", "GATE-167"],
            ["ssrs"] = ["SSRS", "GATE-168"],
            ["fabric"] = ["Fabric", "GATE-170"],
            ["azure"] = ["Azure", "GATE-171"]
        };
        Check(bi.Sections.Select(s => s.Id).SequenceEqual(expected.Keys), "gated-bi-section-set");
        foreach (var s in bi.Sections)
        {
            Check(s.State == GateState.Disabled, $"gated-bi-{s.Id}-disabled");
            Check(s.StatusText.Contains("Disabled", StringComparison.Ordinal), $"gated-bi-{s.Id}-status-disabled");
            var want = expected[s.Id];
            Check(s.Title.Contains(want[0], StringComparison.Ordinal), $"gated-bi-{s.Id}-title");
            Check(want.Skip(1).All(c => s.CapIds.Contains(c)), $"gated-bi-{s.Id}-caps");
            var text = s.MissingRequirement;
            Check(text.Contains("protocol", StringComparison.OrdinalIgnoreCase), $"gated-bi-{s.Id}-names-protocol");
            Check(text.Contains("auth", StringComparison.OrdinalIgnoreCase), $"gated-bi-{s.Id}-names-auth");
            Check(s.Explanation.Contains("No credentials", StringComparison.Ordinal), $"gated-bi-{s.Id}-no-credentials-collected");
        }
        Check(bi.Sections.Single(s => s.Id == "ssas").MissingRequirement.Contains("XMLA", StringComparison.Ordinal), "gated-bi-ssas-xmla");
        Check(bi.Sections.Single(s => s.Id == "ssrs").MissingRequirement.Contains("REST", StringComparison.Ordinal), "gated-bi-ssrs-rest");
        Check(bi.Sections.Single(s => s.Id == "ssisdb").MissingRequirement.Contains("catalog.", StringComparison.Ordinal), "gated-bi-ssisdb-catalog-procs");
        Check(bi.Sections.Single(s => s.Id == "azure").MissingRequirement.Contains("tenant", StringComparison.OrdinalIgnoreCase), "gated-bi-azure-tenant");
        Check(bi.Summary.Contains("No cloud call", StringComparison.Ordinal), "gated-bi-no-cloud-call-statement");
        Pass("gated-bi-cloud");
    }

    static void NoPretendClaims()
    {
        foreach (var w in GatedWorkspaceCatalog.All)
            foreach (var text in AllText(w))
                foreach (var word in ForbiddenClaims)
                    Check(!text.Contains(word, StringComparison.OrdinalIgnoreCase), $"gated-text-no-claim-{w.Id}-{word}: {text}");
        Pass("gated-text-no-pretend-claims");
    }

    static void ViewSourceScans()
    {
        var root = FindRepoRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "Sqloryn.App", "GatedWorkspaceWindow.cs"));
        // Three named views, each bound to its catalog model.
        foreach (var (type, accessor) in new[]
                 {
                     ("HighAvailabilityWorkspace", "GatedWorkspaceCatalog.HighAvailability"),
                     ("ReplicationWorkspace", "GatedWorkspaceCatalog.Replication"),
                     ("BiCloudWorkspace", "GatedWorkspaceCatalog.BiCloud")
                 })
        {
            Check(view.Contains($"class {type}", StringComparison.Ordinal) && view.Contains(accessor, StringComparison.Ordinal), $"gated-view-{type}");
        }

        // Rendered controls come from the model; no hard-coded health/connection claims, no enabled section actions.
        foreach (var word in ForbiddenClaims)
            Check(!view.Contains(word, StringComparison.OrdinalIgnoreCase), $"gated-view-no-claim-literal-{word}");
        Check(view.Contains("IsEnabled = section.ActionEnabled", StringComparison.Ordinal), "gated-view-action-bound-to-model");
        Check(view.Contains("IsEnabled = false", StringComparison.Ordinal), "gated-view-disabled-action");
        Check(!view.Contains("IsEnabled = true", StringComparison.Ordinal), "gated-view-never-force-enabled");
        Check(view.Contains("MissingRequirement", StringComparison.Ordinal) && view.Contains("StatusLine", StringComparison.Ordinal), "gated-view-shows-gate-messages");

        // No credential collection, no network/SQL/process access from the gated views.
        foreach (var banned in new[]
                 {
                     "PasswordChar", "TextBox", "Password", "SqlSession", "SqlConnection", "HttpClient", "WebClient", "Socket",
                     "Process.Start", "Microsoft.Data.SqlClient", "Azure.", "ConnectionOptions", "SecretStore"
                 })
            Check(!view.Contains(banned, StringComparison.Ordinal), $"gated-view-no-{banned}");
        Pass("gated-view-source-scan");
    }

    static void MenuAndDocsScans()
    {
        var root = FindRepoRoot();
        var shell = File.ReadAllText(Path.Combine(root, "src", "Sqloryn.App", "MainWindow.Shell.cs"));
        var toolsStart = shell.IndexOf("var tools = new List<Cmd?>();", StringComparison.Ordinal);
        var toolsEnd = shell.IndexOf("var groups = new List<(string, Cmd?[])>", StringComparison.Ordinal);
        Check(toolsStart >= 0 && toolsEnd > toolsStart, "gated-menu-tools-block-found");
        var tools = shell[toolsStart..toolsEnd];
        foreach (var type in new[] { "HighAvailabilityWorkspace", "ReplicationWorkspace", "BiCloudWorkspace" })
            Check(tools.Contains($"new {type}()", StringComparison.Ordinal), $"gated-menu-opens-{type}");
        Check(tools.Contains("High Availability (Incomplete)", StringComparison.Ordinal), "gated-menu-ha-label-incomplete");
        Check(tools.Contains("Replication (Incomplete)", StringComparison.Ordinal), "gated-menu-repl-label-incomplete");
        Check(tools.Contains("BI and Cloud (Disabled)", StringComparison.Ordinal), "gated-menu-bi-label-disabled");

        var cap = File.ReadAllText(Path.Combine(root, "docs", "ssms", "CAP_EVIDENCE.csv"));
        foreach (var capId in new[] { "GATE-157", "GATE-158", "GATE-163", "GATE-164", "GATE-165", "GATE-166", "GATE-167", "GATE-168", "GATE-170", "GATE-171" })
        {
            var line = cap.Split('\n').Single(l => l.StartsWith(capId + ",", StringComparison.Ordinal));
            Check(line.Contains("incomplete", StringComparison.OrdinalIgnoreCase) && line.Contains("Gated shell", StringComparison.Ordinal), $"gated-cap-note-{capId}");
            Check(line.Contains(",missing,", StringComparison.Ordinal) && line.Contains(",unverified,", StringComparison.Ordinal), $"gated-cap-not-promoted-{capId}");
        }

        var matrix = File.ReadAllText(Path.Combine(root, "docs", "FEATURE_MATRIX.md"));
        Check(matrix.Contains("incomplete until", StringComparison.OrdinalIgnoreCase) && matrix.Contains("Gated shell", StringComparison.Ordinal), "gated-feature-matrix-note");
        Pass("gated-menu-docs-scan");
    }
}
