using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Sqloryn.Core;

/// <summary>Session-only SQL authentication settings. Passwords are never logged.</summary>
public sealed class ConnectionOptions
{
    static readonly Regex HostPattern = new(@"\A[A-Za-z0-9._\-\[\]]{1,253}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    static readonly Regex DatabasePattern = new(@"\A[^[\]]{1,128}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public required string Server { get; init; }
    public int Port { get; init; } = 1433;
    public required string UserId { get; init; }
    public required string Password { get; init; }
    public string Database { get; init; } = "master";
    public bool Encrypt { get; init; } = true;
    public bool TrustServerCertificate { get; init; }
    /// <summary>Optional lab pin path. Distinct from ordinary CA validation; never a silent trust bypass.</summary>
    public string? ServerCertificatePath { get; init; }
    public int ConnectTimeoutSeconds { get; init; } = 15;
    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        if (!HostPattern.IsMatch(Server))
            throw new ArgumentException("Invalid server host.");
        if (Port is < 1 or > 65535)
            throw new ArgumentException("Invalid port.");
        if (string.IsNullOrWhiteSpace(UserId) || UserId.Length > 128 || UserId.Any(char.IsControl))
            throw new ArgumentException("Invalid user id.");
        if (string.IsNullOrEmpty(Password) || Password.Length > 128 || Password.Any(ch => ch is '\0' or '\r' or '\n'))
            throw new ArgumentException("Invalid password.");
        if (!DatabasePattern.IsMatch(Database))
            throw new ArgumentException("Invalid database name.");
        if (!Encrypt)
            throw new ArgumentException("Encryption is mandatory for SQLoryn connections.");
        if (TrustServerCertificate && !string.IsNullOrEmpty(ServerCertificatePath))
            throw new ArgumentException("TrustServerCertificate cannot combine with an explicit server certificate pin.");
        if (!string.IsNullOrEmpty(ServerCertificatePath) && !File.Exists(ServerCertificatePath))
            throw new ArgumentException("Server certificate file was not found.");
        if (ConnectTimeoutSeconds is < 1 or > 120 || CommandTimeoutSeconds is < 1 or > 600)
            throw new ArgumentException("Invalid timeout.");
    }

    internal string BuildConnectionString()
    {
        Validate();
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Port == 1433 ? $"tcp:{Server}" : $"tcp:{Server},{Port.ToString(CultureInfo.InvariantCulture)}",
            InitialCatalog = Database,
            UserID = UserId,
            Password = Password,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = TrustServerCertificate,
            PersistSecurityInfo = false,
            Pooling = false,
            ConnectRetryCount = 0,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = "SQLoryn Developer Preview 0"
        };
        if (!string.IsNullOrEmpty(ServerCertificatePath))
            builder.ServerCertificate = Path.GetFullPath(ServerCertificatePath);
        return builder.ConnectionString;
    }

    public string DisplayTarget => Port == 1433 ? Server : $"{Server},{Port.ToString(CultureInfo.InvariantCulture)}";
}
