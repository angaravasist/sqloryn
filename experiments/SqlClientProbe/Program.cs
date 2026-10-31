using System.Runtime.InteropServices;
using Sqloryn.SqlClientProbe;

if (args is ["--help"])
{
    Console.WriteLine("Developer experiment only. See docs/TESTING.md; launch with scripts/run_sql_probe.py.");
    return 0;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; deadline.Cancel(); };
Console.CancelKeyPress += interrupt;
try
{
    var options = ProbeOptions.Parse(args);
    if (!Console.IsInputRedirected)
        throw new ArgumentException("Password must arrive over stdin.");
    string password = ProbeOptions.ReadPassword(Console.In);
    Console.WriteLine($"Client: {RuntimeInformation.OSArchitecture}/{RuntimeInformation.ProcessArchitecture}; .NET {Environment.Version}");
    await ProbeChecks.RunAsync(options, password, Console.Out, deadline.Token);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(SafeFailure.Describe(error));
    return 1;
}
finally
{
    Console.CancelKeyPress -= interrupt;
}
