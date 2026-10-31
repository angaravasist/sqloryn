using System.Data;
using System.Data.SqlTypes;
using Microsoft.Data.SqlClient;

namespace Sqloryn.SqlClientProbe;

internal static class ProbeChecks
{
    private static void Require(bool condition, string check)
    {
        if (!condition)
            throw new ProbeFailure(check);
    }

    internal static async Task RunAsync(ProbeOptions options, string password, TextWriter output, CancellationToken token)
    {
        await using var connection = new SqlConnection(options.ConnectionString(password));
        await connection.OpenAsync(token);
        using (var identity = new SqlCommand("""
            SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')),
                   CONVERT(nvarchar(128), SERVERPROPERTY('Edition')),
                   CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));
            """, connection))
        {
            identity.CommandTimeout = 5;
            await using var reader = await identity.ExecuteReaderAsync(token);
            Require(await reader.ReadAsync(token), "PROBE-02 identity row");
            Require(reader.GetString(0) == options.ExpectedServer &&
                    reader.GetString(1).StartsWith("Developer Edition", StringComparison.Ordinal),
                    "PROBE-02 disposable identity");
            Require(Version.TryParse(reader.GetString(2), out Version? version) && version.Major == 16,
                    "PROBE-02 SQL Server 2022");
            output.WriteLine($"PASS PROBE-02 SQL Server {version}; Developer edition");
        }

        await using (var wrongPin = new SqlConnection(options.ConnectionString(password, wrongCertificate: true)))
        {
            try
            {
                await wrongPin.OpenAsync(token);
                throw new ProbeFailure("PROBE-03 wrong certificate accepted");
            }
            catch (SqlException error) when (SafeFailure.IsTls(error))
            {
                output.WriteLine("PASS PROBE-03 mismatched certificate rejected during TLS");
            }
        }

        string invalidPassword = password[..^1] + (password[^1] == 'x' ? "y" : "x");
        await using (var wrongPassword = new SqlConnection(options.ConnectionString(invalidPassword)))
        {
            try
            {
                await wrongPassword.OpenAsync(token);
                throw new ProbeFailure("PROBE-04 invalid password accepted");
            }
            catch (SqlException error) when (error.Number == 18456)
            {
                output.WriteLine("PASS PROBE-04 invalid password rejected");
            }
        }

        await CheckResultsAsync(connection, token);
        output.WriteLine("PASS PROBE-05 multiple results, empty set and type fidelity");
        await CheckMessagesAndErrorsAsync(connection, token);
        output.WriteLine("PASS PROBE-06 notices and error after a result");
        await CheckTimeoutAsync(connection, token);
        output.WriteLine("PASS PROBE-07 command timeout");
        await CheckCancellationAsync(options, password, connection, token);
        output.WriteLine("PASS PROBE-08 observed server cancellation and session reuse");
        await CheckStreamingAsync(connection, token);
        output.WriteLine("PASS PROBE-09 10000 sequential synthetic rows");
    }

    private static async Task CheckResultsAsync(SqlConnection connection, CancellationToken token)
    {
        using var command = new SqlCommand("""
            SELECT CAST(NULL AS nvarchar(10)), N'', @text,
                   CAST(12345678901234567890123456789012345678 AS decimal(38,0)),
                   @binary, @timestamp;
            SELECT CAST(1 AS int) WHERE 1 = 0;
            SELECT CAST(42 AS int);
            """, connection) { CommandTimeout = 5 };
        const string text = "SQLoryn Ελληνικά 日本語 🧪";
        byte[] binary = [0, 1, 127, 128, 255];
        var timestamp = new DateTimeOffset(2026, 10, 31, 12, 34, 56, TimeSpan.FromMinutes(330)).AddTicks(1234567);
        command.Parameters.Add("@text", SqlDbType.NVarChar, 100).Value = text;
        command.Parameters.Add("@binary", SqlDbType.VarBinary, binary.Length).Value = binary;
        command.Parameters.Add("@timestamp", SqlDbType.DateTimeOffset).Value = timestamp;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        Require(await reader.ReadAsync(token), "PROBE-05 first row");
        Require(reader.IsDBNull(0) && reader.GetString(1) == "" && reader.GetString(2) == text &&
                reader.GetSqlDecimal(3).Equals(SqlDecimal.Parse("12345678901234567890123456789012345678")) &&
                reader.GetSqlBinary(4).Value.SequenceEqual(binary) &&
                reader.GetDateTimeOffset(5).EqualsExact(timestamp), "PROBE-05 fidelity");
        Require(!await reader.ReadAsync(token), "PROBE-05 first result count");
        Require(await reader.NextResultAsync(token) && reader.FieldCount == 1 &&
                !await reader.ReadAsync(token), "PROBE-05 empty result");
        Require(await reader.NextResultAsync(token) && await reader.ReadAsync(token) &&
                reader.GetInt32(0) == 42 && !await reader.ReadAsync(token) &&
                !await reader.NextResultAsync(token), "PROBE-05 final result");
    }

    private static async Task CheckMessagesAndErrorsAsync(SqlConnection connection, CancellationToken token)
    {
        int notices = 0;
        SqlInfoMessageEventHandler handler = (_, e) => notices += e.Errors.Count;
        connection.InfoMessage += handler;
        try
        {
            using var command = new SqlCommand("PRINT N'SQLoryn synthetic notice'; SELECT 1; THROW 50000, 'SQLoryn synthetic error', 1;", connection)
            { CommandTimeout = 5 };
            bool sawRow = false;
            try
            {
                await using var reader = await command.ExecuteReaderAsync(token);
                do
                {
                    while (await reader.ReadAsync(token))
                        sawRow = true;
                } while (await reader.NextResultAsync(token));
                throw new ProbeFailure("PROBE-06 late error missing");
            }
            catch (SqlException error) when (error.Number == 50000)
            {
                Require(sawRow && notices > 0, "PROBE-06 row and notice before error");
            }
        }
        finally
        {
            connection.InfoMessage -= handler;
        }
    }

    private static async Task CheckTimeoutAsync(SqlConnection connection, CancellationToken token)
    {
        using var command = new SqlCommand("WAITFOR DELAY '00:00:05';", connection) { CommandTimeout = 1 };
        try
        {
            await command.ExecuteNonQueryAsync(token);
            throw new ProbeFailure("PROBE-07 timeout missing");
        }
        catch (SqlException error) when (error.Number == -2 && !token.IsCancellationRequested) { }
    }

    private static async Task CheckCancellationAsync(ProbeOptions options, string password, SqlConnection observer, CancellationToken token)
    {
        await using var worker = new SqlConnection(options.ConnectionString(password));
        await worker.OpenAsync(token);
        using var session = new SqlCommand("SELECT @@SPID;", worker) { CommandTimeout = 5 };
        int sessionId = Convert.ToInt32(await session.ExecuteScalarAsync(token));
        using var activity = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = @session;", observer)
        { CommandTimeout = 5 };
        activity.Parameters.Add("@session", SqlDbType.Int).Value = sessionId;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var command = new SqlCommand("WAITFOR DELAY '00:00:20';", worker) { CommandTimeout = 25 };
        Task execution = command.ExecuteNonQueryAsync(cancel.Token);
        try
        {
            bool active = false;
            for (int attempt = 0; attempt < 50 && !active; attempt++)
            {
                active = Convert.ToInt32(await activity.ExecuteScalarAsync(token)) > 0;
                if (!active)
                    await Task.Delay(100, token);
            }
            Require(active && !execution.IsCompleted, "PROBE-08 active server request");
            cancel.Cancel();
            try
            {
                await execution.WaitAsync(TimeSpan.FromSeconds(5), token);
                throw new ProbeFailure("PROBE-08 execution did not cancel");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (SqlException error) when (error.Number == 0 && !token.IsCancellationRequested) { }
            Require(Convert.ToInt32(await activity.ExecuteScalarAsync(token)) == 0, "PROBE-08 server request still active");
            using var state = new SqlCommand("SELECT @@SPID, @@TRANCOUNT, XACT_STATE();", worker) { CommandTimeout = 5 };
            await using var reader = await state.ExecuteReaderAsync(token);
            Require(await reader.ReadAsync(token) && Convert.ToInt32(reader.GetValue(0)) == sessionId &&
                    reader.GetInt32(1) == 0 && reader.GetInt16(2) == 0, "PROBE-08 session reuse");
        }
        finally
        {
            cancel.Cancel();
            // Observe the outstanding task even if the observer or assertion fails.
            try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is OperationCanceledException or SqlException or TimeoutException) { }
        }
    }

    private static async Task CheckStreamingAsync(SqlConnection connection, CancellationToken token)
    {
        using var command = new SqlCommand("""
            WITH numbers AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM numbers WHERE n < 10000)
            SELECT n FROM numbers ORDER BY n OPTION (MAXRECURSION 0);
            """, connection) { CommandTimeout = 10 };
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        int rows = 0;
        while (await reader.ReadAsync(token))
            Require(reader.GetInt32(0) == ++rows, "PROBE-09 row order/value");
        Require(rows == 10000 && !await reader.NextResultAsync(token), "PROBE-09 complete result");
    }
}
