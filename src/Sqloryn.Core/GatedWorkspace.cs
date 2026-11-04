namespace Sqloryn.Core;

/// <summary>
/// Why a capability section is not usable. Neither value is a working state: <see cref="Incomplete"/> means the UI/model
/// work may exist but the real operation has not been verified; <see cref="Disabled"/> means no protocol client or
/// authentication path exists at all. There is deliberately no "available" value.
/// </summary>
public enum GateState
{
    Incomplete,
    Disabled
}

/// <summary>One gated capability. It carries text and CAP traceability only: no connection, credential or health value.</summary>
public sealed record GateSection(
    string Id,
    string Title,
    GateState State,
    string StatusText,
    string Explanation,
    string MissingRequirement,
    IReadOnlyList<string> CapIds)
{
    /// <summary>Always false: a gated section never offers an operation until the real fixture/protocol is verified.</summary>
    public bool ActionEnabled => false;

    public string ActionLabel => State == GateState.Disabled ? "Unavailable (disabled)" : "Unavailable (incomplete)";
}

/// <summary>A workspace made only of gated sections (DELIVERY-CONTRACT §10-11). Never reports connection or health.</summary>
public sealed record GatedWorkspace(
    string Id,
    string Title,
    string StatusLine,
    string Summary,
    IReadOnlyList<GateSection> Sections);

/// <summary>
/// Honest shells for high availability, replication and BI/cloud tooling. Each section names the concrete missing
/// topology, protocol or authentication. Nothing here collects credentials or makes a network call; the sections stay
/// gated until the real operation is verified against an isolated target (see docs/ssms/CAP_EVIDENCE.csv).
/// </summary>
public static class GatedWorkspaceCatalog
{
    const string NoHealth = "No health, role or synchronisation status is shown because none has been read from a server.";
    const string NoCredentials = "No credentials are requested or stored here, and nothing is sent to any service.";

    static GateSection Ha(string id, string title, string explanation, string missing, params string[] caps) =>
        new(id, title, GateState.Incomplete, "Not verified: needs an isolated multi-node fixture",
            explanation + " " + NoHealth, missing, caps);

    static GateSection Bi(string id, string title, string explanation, string missing, params string[] caps) =>
        new(id, title, GateState.Disabled, "Disabled: protocol and authentication not implemented",
            explanation + " " + NoCredentials, missing, caps);

    public static GatedWorkspace HighAvailability { get; } = new(
        "HighAvailabilityWorkspace",
        "High availability",
        "Incomplete: nothing in this window has been verified against a real topology.",
        "Failover, role changes and topology edits need an explicit impact preview and acceptance on an isolated multi-node " +
        "fixture; a single-server mock cannot qualify them. Until that fixture exists these sections stay incomplete.",
        [
            Ha("availability-groups", "Availability Groups",
                "Replica roles, database synchronisation state and failover preview are not implemented.",
                "Needs an isolated multi-node fixture: at least two disposable SQL Server nodes joined in a test availability group, " +
                "plus a permission model for the AG catalog and dynamic management views.",
                "CAP-158"),
            Ha("log-shipping", "Log shipping",
                "Primary/secondary configuration, backup-copy-restore jobs and latency monitoring are not implemented.",
                "Needs a primary node and a secondary node with a shared disposable backup location and SQL Server Agent enabled on both.",
                "CAP-158"),
            Ha("database-mirroring", "Database mirroring (legacy)",
                "Mirroring state and endpoint inspection are not implemented; the pinned comparator support level is unconfirmed.",
                "Needs a principal node, a mirror node and an optional witness node on a version that still exposes mirroring, plus endpoint permissions.",
                "CAP-158")
        ]);

    public static GatedWorkspace Replication { get; } = new(
        "ReplicationWorkspace",
        "Replication",
        "Incomplete: nothing in this window has been verified against a real topology.",
        "Replication needs a multi-node fixture with distinct publisher, distributor and subscriber roles. Topology changes need an " +
        "impact preview first. Until that fixture exists these sections stay incomplete.",
        [
            Ha("publisher", "Publisher and publications",
                "Publication and article listing is not implemented.",
                "Needs a publisher node with a configured distributor and a disposable published database with synthetic data.",
                "CAP-157"),
            Ha("distributor", "Distributor",
                "Distribution database and queue inspection is not implemented.",
                "Needs a distributor node, either local to the publisher or remote, with permission to read the distribution database.",
                "CAP-157"),
            Ha("subscriber", "Subscribers and subscriptions",
                "Subscription listing and initialisation state are not implemented.",
                "Needs at least one separate subscriber node for push and pull subscriptions.",
                "CAP-157"),
            Ha("agents", "Replication agents and monitoring",
                "Snapshot, log reader and distribution agent state and history are not implemented.",
                "Needs running replication agents on a multi-node topology with SQL Server Agent enabled on every node involved.",
                "CAP-157"),
            Ha("conflicts", "Conflict visibility",
                "Conflict tables and resolver policy views are not implemented.",
                "Needs a merge or peer-to-peer topology with deliberately induced synthetic conflicts across at least two writable nodes.",
                "CAP-157")
        ]);

    public static GatedWorkspace BiCloud { get; } = new(
        "BiCloudWorkspace",
        "BI and cloud",
        "Disabled: no BI or cloud service protocol or sign-in is implemented.",
        "Each service needs its own protocol client, permissions and sign-in design. No cloud call is made from this window, no " +
        "credentials are collected, and no tenant, subscription or resource is selected.",
        [
            Bi("ssisdb", "SSISDB catalog and packages",
                "Catalog browsing, deployment and package execution are not implemented.",
                "Missing protocol: T-SQL access to the SSISDB catalog.* procedures and views on a server with Integration Services " +
                "configured. Missing auth: a least-privilege SSISDB role design and a disposable SSISDB fixture.",
                "CAP-163", "CAP-164"),
            Bi("ssas", "SSAS (Analysis Services)",
                "Connection, MDX/DAX queries and processing are not implemented.",
                "Missing protocol: an XMLA over TCP/HTTP client for Analysis Services (not SQL Server TDS). Missing auth: " +
                "Analysis Services server-admin or database-role authentication and a disposable tabular or multidimensional instance.",
                "CAP-165", "CAP-166", "CAP-167"),
            Bi("ssrs", "SSRS (Reporting Services)",
                "Report server administration is not implemented.",
                "Missing protocol: the Reporting Services REST API (or SOAP management endpoint) on a reachable report server. " +
                "Missing auth: report server Windows/NTLM or custom authentication, which this app does not support.",
                "CAP-168"),
            Bi("fabric", "Microsoft Fabric",
                "Workspace and SQL endpoint browsing is not implemented.",
                "Missing protocol: Fabric REST API and the SQL analytics endpoint over TDS. Missing auth: Microsoft Entra ID sign-in " +
                "with explicit tenant and workspace selection, and a data-boundary decision.",
                "CAP-170"),
            Bi("azure", "Azure resource browsing",
                "Subscription, resource group and server listing is not implemented.",
                "Missing protocol: Azure Resource Manager REST API. Missing auth: Microsoft Entra ID sign-in with explicit tenant and " +
                "subscription selection; no subscription or paid resource is implied.",
                "CAP-171")
        ]);

    public static IReadOnlyList<GatedWorkspace> All { get; } = [HighAvailability, Replication, BiCloud];
}
