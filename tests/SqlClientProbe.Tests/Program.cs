using System.Security.Authentication;
using Microsoft.Data.SqlClient;
using Sqloryn.SqlClientProbe;

int passed = 0;
void Check(bool value)
{
    if (!value) throw new Exception("Component assertion failed; values suppressed.");
    passed++;
}
void Reject(Action action)
{
    try { action(); }
    catch (ArgumentException) { passed++; return; }
    throw new Exception("Invalid input accepted.");
}

string certificate = Path.GetTempFileName();
string wrongCertificate = Path.GetTempFileName();
try
{
    string[] arguments = ["--confirm-disposable", "--port", "14330", "--expected-server",
        "sqloryn-probe-0123456789abcdef", "--certificate", certificate, "--wrong-certificate", wrongCertificate];
    var options = ProbeOptions.Parse(arguments);
    string secret = Guid.NewGuid().ToString("N") + ";Encrypt=false;";
    var settings = new SqlConnectionStringBuilder(options.ConnectionString(secret));
    Check(settings.Encrypt == SqlConnectionEncryptOption.Mandatory && !settings.TrustServerCertificate);
    Check(!settings.PersistSecurityInfo && !settings.Pooling && settings.ConnectRetryCount == 0);
    Check(settings.DataSource == "tcp:127.0.0.1,14330" && settings.InitialCatalog == "master");
    Check(settings.Password == secret && settings.UserID == "sa");
    Check(settings.ServerCertificate == Path.GetFullPath(certificate));
    Check(new SqlConnectionStringBuilder(options.ConnectionString(secret, true)).ServerCertificate == Path.GetFullPath(wrongCertificate));
    Check(!options.ToString().Contains(secret, StringComparison.Ordinal));
    Check(!Directory.EnumerateFiles(AppContext.BaseDirectory, "*SNI*.dll", SearchOption.AllDirectories).Any());

    Reject(() => ProbeOptions.Parse([]));
    Reject(() => ProbeOptions.Parse(arguments[1..]));
    foreach (string invalidPort in new[] { "0", "1023", "65536", "-1", " 14330", "14330;Encrypt=false", "" })
    {
        string[] invalid = (string[])arguments.Clone();
        invalid[2] = invalidPort;
        Reject(() => ProbeOptions.Parse(invalid));
    }
    foreach (string invalidServer in new[] { "production", "sqloryn-probe-0123456789abcdef\n", "sqloryn-probe-0123456789abcdeg" })
    {
        string[] invalid = (string[])arguments.Clone();
        invalid[4] = invalidServer;
        Reject(() => ProbeOptions.Parse(invalid));
    }
    string[] duplicate = (string[])arguments.Clone();
    duplicate[3] = "--port";
    Reject(() => ProbeOptions.Parse(duplicate));
    string[] samePin = (string[])arguments.Clone();
    samePin[8] = certificate;
    Reject(() => ProbeOptions.Parse(samePin));
    string[] missingPin = (string[])arguments.Clone();
    missingPin[6] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Reject(() => ProbeOptions.Parse(missingPin));

    Check(ProbeOptions.ReadPassword(new StringReader(secret + "\n")) == secret);
    Check(ProbeOptions.ReadPassword(new StringReader(new string('x', 128))).Length == 128);
    Reject(() => ProbeOptions.ReadPassword(new StringReader("")));
    Reject(() => ProbeOptions.ReadPassword(new StringReader(new string('x', 129))));
    Reject(() => ProbeOptions.ReadPassword(new StringReader("secret\r\n")));
    Check(SafeFailure.IsTls(new Exception(secret, new AuthenticationException(secret))));
    Check(!SafeFailure.IsTls(new IOException(secret)));
    foreach (Exception error in new Exception[] { new Exception(secret), new ArgumentException(secret),
                 new IOException(secret), new AuthenticationException(secret), new OperationCanceledException(secret) })
        Check(!SafeFailure.Describe(error).Contains(secret, StringComparison.Ordinal));
    Console.WriteLine($"PASS {passed} component assertions (E1; no SQL Server connection)");
    return 0;
}
finally
{
    File.Delete(certificate);
    File.Delete(wrongCertificate);
}
