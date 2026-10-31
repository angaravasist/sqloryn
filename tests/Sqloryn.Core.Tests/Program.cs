using System.Data;
using Sqloryn.Core;

static void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception($"FAIL {name}");
}

static void Pass(string name) => Console.WriteLine($"PASS {name}");

try
{
    new ConnectionOptions { Server = "localhost", UserId = "sa", Password = "x", Encrypt = false }.Validate();
    throw new Exception("FAIL encrypt-required");
}
catch (ArgumentException) { Pass("encrypt-required"); }

try
{
    new ConnectionOptions
    {
        Server = "localhost",
        UserId = "sa",
        Password = "x",
        TrustServerCertificate = true,
        ServerCertificatePath = "/tmp/does-not-exist.crt"
    }.Validate();
    throw new Exception("FAIL pin-and-trust-mutex");
}
catch (ArgumentException) { Pass("pin-and-trust-mutex"); }

var opts = new ConnectionOptions
{
    Server = "db.example",
    Port = 14333,
    UserId = "app",
    Password = "Aa1!secret",
    Database = "appdb",
    Encrypt = true
};
opts.Validate();
Check(opts.DisplayTarget == "db.example,14333", "display-target");
Pass("connection-options");

Check(SqlIdentity.QuoteIdentifier("Name]") == "[Name]]]", "quote-identifier");
Check(SqlIdentity.Qualify("dbo", "Orders") == "[dbo].[Orders]", "qualify");
Pass("sql-identity");

Check(ScriptDocument.ResolveBatch("full", null) == "full", "batch-document");
Check(ScriptDocument.ResolveBatch("full", "sel") == "sel", "batch-selection");
var doc = new ScriptDocument();
doc.SetText("a");
Check(doc.IsDirty, "dirty");
doc.DiscardChangesTo("a");
Check(!doc.IsDirty, "discard-clears-dirty");
Pass("script-document");

Check(SqlSession.LooksLikeGoBatch("SELECT 1;\nGO\nSELECT 2;"), "go-detect");
Check(!SqlSession.LooksLikeGoBatch("SELECT 'GO' AS g;"), "go-string-not-batch");
Pass("go-policy");

var table = new DataTable();
table.Columns.Add("a", typeof(string));
table.Columns.Add("b", typeof(string));
table.Columns.Add("c", typeof(string));
table.Columns.Add("d", typeof(decimal));
table.Rows.Add(DBNull.Value, "", "hi", 1.23m);
using (var reader = table.CreateDataReader())
{
    Check(reader.Read(), "reader-row");
    Check(SqlSession.FormatCell(reader, 0).IsNull, "null-cell");
    Check(!SqlSession.FormatCell(reader, 1).IsNull && SqlSession.FormatCell(reader, 1).Kind == "empty-string", "empty-string-cell");
    Check(SqlSession.FormatCell(reader, 2).Display == "hi", "text-cell");
    Check(SqlSession.FormatCell(reader, 3).Kind == "decimal", "decimal-cell");
}
Pass("cell-format");

await using (var session = new SqlSession())
{
    Check(!session.IsConnected, "session-starts-disconnected");
    try
    {
        await session.ExecuteAsync("SELECT 1");
        throw new Exception("FAIL execute-without-connect");
    }
    catch (InvalidOperationException) { Pass("execute-without-connect"); }
}

var tabA = new ScriptDocument();
tabA.SetText("SELECT 'A'");
var tabB = new ScriptDocument();
tabB.SetText("SELECT 'B'");
Check(ScriptDocument.ResolveBatch(tabA.Text, null) != ScriptDocument.ResolveBatch(tabB.Text, null), "tab-sql-isolation");
Pass("tab-sql-isolation");

Console.WriteLine("PASS Sqloryn.Core component assertions (E1; no SQL Server connection)");
return 0;
