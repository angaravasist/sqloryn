using System.Globalization;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Sqloryn.SqlClientProbe;

internal sealed record ProbeOptions(int Port, string ExpectedServer, string Certificate, string WrongCertificate)
{
    internal static ProbeOptions Parse(string[] args)
    {
        if (args.Length != 9 || args[0] != "--confirm-disposable")
            throw new ArgumentException("Use the disposable test runner.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2)
            if (!values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Duplicate argument.");

        if (!values.TryGetValue("--port", out string? portText) ||
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
            port is < 1024 or > 65535 ||
            !values.TryGetValue("--expected-server", out string? server) ||
            !Regex.IsMatch(server, "\\Asqloryn-probe-[a-f0-9]{16}\\z", RegexOptions.CultureInvariant) ||
            !values.TryGetValue("--certificate", out string? certificate) ||
            !values.TryGetValue("--wrong-certificate", out string? wrongCertificate) ||
            !File.Exists(certificate) || !File.Exists(wrongCertificate) ||
            Path.GetFullPath(certificate) == Path.GetFullPath(wrongCertificate))
            throw new ArgumentException("Invalid disposable test configuration.");

        return new(port, server, Path.GetFullPath(certificate), Path.GetFullPath(wrongCertificate));
    }

    internal string ConnectionString(string password, bool wrongCertificate = false) => new SqlConnectionStringBuilder
    {
        DataSource = $"tcp:127.0.0.1,{Port}",
        InitialCatalog = "master",
        UserID = "sa",
        Password = password,
        Encrypt = SqlConnectionEncryptOption.Mandatory,
        TrustServerCertificate = false,
        ServerCertificate = wrongCertificate ? WrongCertificate : Certificate,
        PersistSecurityInfo = false,
        Pooling = false,
        ConnectRetryCount = 0,
        ConnectTimeout = 5,
        ApplicationName = "SQLoryn disposable probe"
    }.ConnectionString;

    internal static string ReadPassword(TextReader input)
    {
        var password = new StringBuilder();
        int next;
        while ((next = input.Read()) is not (-1 or '\n'))
        {
            if (password.Length == 128 || char.IsControl((char)next))
                throw new ArgumentException("Invalid password input.");
            password.Append((char)next);
        }
        if (password.Length == 0)
            throw new ArgumentException("Missing password input.");
        return password.ToString();
    }
}

internal sealed class ProbeFailure(string check) : Exception
{
    internal string Check { get; } = check;
}

internal static class SafeFailure
{
    internal static bool IsTls(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is AuthenticationException)
                return true;
        return false;
    }

    internal static string Describe(Exception error) => error switch
    {
        ProbeFailure failure => $"FAIL {failure.Check}",
        ArgumentException => "FAIL configuration rejected; use the disposable test runner.",
        OperationCanceledException => "FAIL interrupted; server completion may be unknown.",
        _ when IsTls(error) => "FAIL TLS authentication; check the disposable server certificate.",
        SqlException sql => $"FAIL SQL request (number {sql.Number}); sensitive diagnostics suppressed.",
        _ => $"FAIL probe ({error.GetType().Name}); sensitive diagnostics suppressed."
    };
}
