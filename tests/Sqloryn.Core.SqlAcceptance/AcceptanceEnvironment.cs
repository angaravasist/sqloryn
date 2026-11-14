using System.Globalization;
using System.Text;

namespace Sqloryn.Core.SqlAcceptance;

/// <summary>
/// Connection settings for the disposable SQL Server, read from SQLORYN_ACCEPTANCE_* environment variables.
/// The same rules as <see cref="ConnectionOptions"/> apply: encryption is mandatory and a certificate pin and
/// TrustServerCertificate are mutually exclusive. The password exists only in this process's memory.
/// </summary>
sealed record AcceptanceEnvironment(
    string Server, int Port, string User, string Password, string Database, string? CertificatePath, bool Trust)
{
    internal static readonly string[] RequiredNames =
    [
        "SQLORYN_ACCEPTANCE_SERVER", "SQLORYN_ACCEPTANCE_PORT", "SQLORYN_ACCEPTANCE_USER",
        "SQLORYN_ACCEPTANCE_PASSWORD", "SQLORYN_ACCEPTANCE_DATABASE"
    ];

    internal const string CertName = "SQLORYN_ACCEPTANCE_CERT";
    internal const string TrustName = "SQLORYN_ACCEPTANCE_TRUST";

    public enum Kind { Absent, Partial, Complete }

    public static Kind Classify(Func<string, string?> get)
    {
        var present = RequiredNames.Count(name => !string.IsNullOrEmpty(get(name)));
        return present == 0 ? Kind.Absent : present == RequiredNames.Length ? Kind.Complete : Kind.Partial;
    }

    public static string[] MissingNames(Func<string, string?> get)
        => RequiredNames.Where(name => string.IsNullOrEmpty(get(name))).ToArray();

    public static AcceptanceEnvironment Read(Func<string, string?> get)
    {
        if (!int.TryParse(get("SQLORYN_ACCEPTANCE_PORT"), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw new ArgumentException("SQLORYN_ACCEPTANCE_PORT is not a valid port.");
        var cert = get(CertName);
        var trustText = get(TrustName);
        var trust = trustText is "1" || string.Equals(trustText, "true", StringComparison.OrdinalIgnoreCase);
        return new AcceptanceEnvironment(
            get("SQLORYN_ACCEPTANCE_SERVER")!, port, get("SQLORYN_ACCEPTANCE_USER")!,
            get("SQLORYN_ACCEPTANCE_PASSWORD")!, get("SQLORYN_ACCEPTANCE_DATABASE")!,
            string.IsNullOrEmpty(cert) ? null : cert, trust);
    }

    public ConnectionOptions ToOptions(string? userOverride = null, string? passwordOverride = null)
    {
        var options = new ConnectionOptions
        {
            Server = Server,
            Port = Port,
            UserId = userOverride ?? User,
            Password = passwordOverride ?? Password,
            Database = Database,
            Encrypt = true,
            TrustServerCertificate = Trust,
            ServerCertificatePath = CertificatePath,
            ConnectTimeoutSeconds = 15,
            CommandTimeoutSeconds = 30
        };
        options.Validate();
        return options;
    }
}

/// <summary>Removes secrets from any text before it reaches the console.</summary>
sealed class SecretRedactor
{
    readonly List<string> _secrets = [];

    public void Add(string secret)
    {
        if (!string.IsNullOrEmpty(secret))
            _secrets.Add(secret);
    }

    public string Clean(string text)
    {
        var sb = new StringBuilder(text.Replace('\r', ' ').Replace('\n', ' '));
        foreach (var secret in _secrets)
            sb.Replace(secret, "***");
        var cleaned = sb.ToString();
        return cleaned.Length <= 400 ? cleaned : cleaned[..400] + "…";
    }
}
