using Sqloryn.Core.SqlAcceptance;

// Core live acceptance against a disposable SQL Server. See docs/TESTING.md.
// Not a unit test: it only runs when scripts/run_sql_probe.py (or a maintainer) supplies SQLORYN_ACCEPTANCE_*.
static string? Get(string name) => Environment.GetEnvironmentVariable(name);

switch (AcceptanceEnvironment.Classify(Get))
{
    case AcceptanceEnvironment.Kind.Absent:
        Console.WriteLine("SKIP CORE-ACCEPTANCE environment not configured; Core live acceptance was NOT run (no SQL claim).");
        return 0;
    case AcceptanceEnvironment.Kind.Partial:
        // A half-configured run must not look like a skip: the runner always supplies every variable.
        Console.Error.WriteLine("FAIL CORE-ACCEPTANCE incomplete configuration; missing: " +
                                string.Join(", ", AcceptanceEnvironment.MissingNames(Get)));
        return 2;
}

var redactor = new SecretRedactor();
AcceptanceEnvironment environment;
try
{
    environment = AcceptanceEnvironment.Read(Get);
    redactor.Add(environment.Password);
    environment.ToOptions(); // same validation as the app (mandatory encryption, pin xor trust).
}
catch (ArgumentException error)
{
    Console.Error.WriteLine("FAIL CORE-ACCEPTANCE invalid configuration: " + redactor.Clean(error.Message));
    return 2;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; deadline.Cancel(); };
Console.CancelKeyPress += interrupt;
try
{
    var run = new AcceptanceRun(environment, redactor, Console.Out);
    return await run.ExecuteAsync(deadline.Token);
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL CORE-ACCEPTANCE aborted ({error.GetType().Name}): {redactor.Clean(error.Message)}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= interrupt;
}
