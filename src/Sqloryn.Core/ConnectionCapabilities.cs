using Microsoft.Data.SqlClient;

namespace Sqloryn.Core;

/// <summary>Runs one lightweight read-only probe query and returns its rows. Implementations must not retry.</summary>
public interface ICapabilityQueryRunner
{
    Task<IReadOnlyList<object?[]>> QueryAsync(string sql, CancellationToken cancellationToken);
}

/// <summary>
/// What a successful login says about the target. A login is not a capability grant: SHOWPLAN permission and
/// SQL Server Agent availability are <c>null</c> (unknown) until <see cref="ProbeAsync"/> observes them, and stay
/// <c>null</c> when the probe is denied or fails. Probing never fails the session.
/// </summary>
public sealed record ConnectionCapabilities
{
    public string? ProductVersion { get; init; }
    public string? Edition { get; init; }
    public int? EngineEdition { get; init; }
    /// <summary>True/false once observed; null = unknown (not probed, denied or probe failed).</summary>
    public bool? HasShowplanPermission { get; init; }
    /// <summary>True/false once observed; null = unknown.</summary>
    public bool? SupportsAgent { get; init; }
    public bool Probed { get; init; }
    /// <summary>Safe, server-text-free reasons why a capability is still unknown.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public static ConnectionCapabilities Unprobed(string? productVersion) => new() { ProductVersion = productVersion };

    internal const string IdentitySql =
        "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), CAST(SERVERPROPERTY('EngineEdition') AS int);";
    internal const string ShowplanSql =
        "SELECT CAST(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'SHOWPLAN') AS int);";
    internal const string AgentSql =
        "SELECT COUNT(*) FROM sys.dm_server_services WHERE servicename LIKE N'SQL Server Agent%';";

    // Azure SQL Database, Synapse, Express, Azure SQL Edge and serverless pools have no SQL Server Agent service.
    static readonly HashSet<int> EnginesWithoutAgent = [4, 5, 6, 9, 11];

    /// <summary>
    /// Runs the probe queries independently. A failure of any query leaves only that capability unknown.
    /// Only caller cancellation propagates.
    /// </summary>
    public static async Task<ConnectionCapabilities> ProbeAsync(
        ICapabilityQueryRunner runner, string? connectProductVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var notes = new List<string>();
        string? version = connectProductVersion, edition = null;
        int? engine = null;
        bool? showplan = null, agent = null;

        var identity = await TryAsync("Server identity", IdentitySql).ConfigureAwait(false);
        if (identity is { Count: > 0 } && identity[0].Length >= 3)
        {
            version = AsString(identity[0][0]) ?? version;
            edition = AsString(identity[0][1]);
            engine = AsInt(identity[0][2]);
        }

        var sp = await TryAsync("SHOWPLAN permission", ShowplanSql).ConfigureAwait(false);
        if (sp is { Count: > 0 } && sp[0].Length >= 1)
        {
            var flag = AsInt(sp[0][0]);
            showplan = flag is null ? null : flag != 0;
            if (flag is null)
                notes.Add("SHOWPLAN permission could not be determined by the server.");
        }

        if (engine is { } e && EnginesWithoutAgent.Contains(e))
        {
            agent = false;
            notes.Add("This engine edition does not include SQL Server Agent.");
        }
        else
        {
            var ag = await TryAsync("SQL Server Agent", AgentSql).ConfigureAwait(false);
            if (ag is { Count: > 0 } && ag[0].Length >= 1 && AsInt(ag[0][0]) is { } count)
                agent = count > 0;
        }

        return new ConnectionCapabilities
        {
            ProductVersion = version,
            Edition = edition,
            EngineEdition = engine,
            HasShowplanPermission = showplan,
            SupportsAgent = agent,
            Probed = true,
            Notes = notes
        };

        async Task<IReadOnlyList<object?[]>?> TryAsync(string what, string sql)
        {
            try
            {
                return await runner.QueryAsync(sql, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Classify only; raw exception text can carry server names or object details.
                notes.Add(Classify(ex) == FailureKind.PermissionDenied
                    ? $"{what}: permission denied; treated as unknown."
                    : $"{what}: probe unavailable; treated as unknown.");
                return null;
            }
        }
    }

    enum FailureKind { PermissionDenied, Unavailable }

    static FailureKind Classify(Exception ex)
        => ex is UnauthorizedAccessException || (ex is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => IsPermissionDeniedNumber(e.Number)))
            ? FailureKind.PermissionDenied
            : FailureKind.Unavailable;

    /// <summary>SQL Server error numbers meaning "permission denied / not authorised".</summary>
    public static bool IsPermissionDeniedNumber(int number) => number is 229 or 262 or 297 or 300 or 371 or 916 or 15151;

    static string? AsString(object? value) => value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

    static int? AsInt(object? value)
        => value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Plain-language TLS posture for the connect dialog. Shows file names only, never full paths.</summary>
public static class TlsSummary
{
    public static string Describe(bool trustServerCertificate, string? serverCertificatePath)
    {
        var hasPin = !string.IsNullOrWhiteSpace(serverCertificatePath);
        if (trustServerCertificate && hasPin)
            return "TLS: Trust server certificate and a certificate pin cannot be combined; choose one.";
        if (trustServerCertificate)
            return "TLS: encrypted, but the server certificate is NOT validated (lab only; no CA or server-name check).";
        if (hasPin)
            return $"TLS: encryption mandatory; server certificate pinned to {Path.GetFileName(serverCertificatePath!.Trim())}.";
        return "TLS: encryption mandatory; server certificate chain and name validated against the system trust store.";
    }
}
