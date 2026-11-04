using Sqloryn.Core;

/// <summary>
/// CAP-043 object properties, CAP-034 user-defined types, CAP-096 XML/JSON viewer,
/// CAP-078 long-running status. Component-only: no SQL Server connection.
/// </summary>
static class ContCapsTests
{
    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"FAIL {name}");
    }

    static void Pass(string name) => Console.WriteLine($"PASS {name}");

    static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"FAIL {name}");
    }

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SQLoryn.slnx")))
                return d.FullName;
        throw new Exception("FAIL repo root (SQLoryn.slnx) not found from test base directory");
    }

    public static void Run()
    {
        ObjectProperties();
        UserDefinedTypes();
        StructuredValue();
        LongRunning();
        Wiring();
        Pass("cont-caps");
    }

    static void ObjectProperties()
    {
        var sql = MetadataCatalog.ObjectFacts("My]Db", "dbo'; DROP TABLE x;--", "Ord]ers");
        Check(sql.Contains("[My]]Db].sys.objects", StringComparison.Ordinal), "cap043-sql-quotes-database");
        Check(sql.Contains("@schema", StringComparison.Ordinal) && sql.Contains("@name", StringComparison.Ordinal), "cap043-sql-parameters");
        Check(!sql.Contains("DROP", StringComparison.Ordinal) && !sql.Contains("Ord]ers", StringComparison.Ordinal), "cap043-sql-does-not-inline-names");
        Check(sql.Contains("create_date", StringComparison.Ordinal) && sql.Contains("modify_date", StringComparison.Ordinal), "cap043-sql-dates");
        Check(!sql.Contains("USE ", StringComparison.Ordinal), "cap043-sql-no-use");
        foreach (var word in new[] { "INSERT", "UPDATE", "DELETE", "ALTER", "MERGE", "EXEC", "TRUNCATE" })
            Check(!sql.Contains(word, StringComparison.OrdinalIgnoreCase), "cap043-sql-read-only-" + word);

        var database = MetadataCatalog.DatabaseFacts("App]Db");
        Check(database.Contains("sys.databases", StringComparison.Ordinal) && database.Contains("create_date", StringComparison.Ordinal), "cap043-database-sql");
        Check(database.Contains("@name", StringComparison.Ordinal) && !database.Contains("App]Db", StringComparison.Ordinal), "cap043-database-name-parameter");
        Check(!database.Contains("modify_date", StringComparison.Ordinal), "cap043-database-has-no-modify-column");
        Check(!database.Contains("USE ", StringComparison.Ordinal), "cap043-database-no-use");

        Throws<ArgumentException>(() => MetadataCatalog.ObjectFacts("", "dbo", "T"), "cap043-bad-database");
        Throws<ArgumentException>(() => MetadataCatalog.ObjectFacts("d", "bad\0", "T"), "cap043-nul-schema");
        Throws<ArgumentException>(() => MetadataCatalog.DatabaseFacts(""), "cap043-blank-database-name");

        var created = new DateTime(2020, 1, 2, 3, 4, 5, 6);
        var modified = new DateTime(2021, 5, 6, 7, 8, 9, 10);
        var rows = new RawRows(
            ["schema", "name", "type_desc", "create_date", "modify_date"],
            ["nvarchar", "nvarchar", "nvarchar", "datetime", "datetime"],
            [["dbo", "Orders", "USER_TABLE", created, modified]],
            false);
        var facts = ObjectPropertyCatalog.ParseObject(rows);
        Check(facts is { Schema: "dbo", Name: "Orders", TypeName: "USER_TABLE" } &&
              facts.Created == created && facts.Modified == modified, "cap043-parse-object");
        var text = ObjectPropertySheet.FormatCatalogObject(facts!);
        Check(text.Contains("Name", StringComparison.Ordinal) && text.Contains("Orders", StringComparison.Ordinal), "cap043-sheet-name");
        Check(text.Contains("Schema", StringComparison.Ordinal) && text.Contains("dbo", StringComparison.Ordinal), "cap043-sheet-schema");
        Check(text.Contains("Type", StringComparison.Ordinal) && text.Contains("USER_TABLE", StringComparison.Ordinal), "cap043-sheet-type");
        Check(text.Contains("2020-01-02 03:04:05.006", StringComparison.Ordinal), "cap043-sheet-created");
        Check(text.Contains("2021-05-06 07:08:09.010", StringComparison.Ordinal), "cap043-sheet-modified");
        Check(text.Contains("Nothing was changed", StringComparison.Ordinal), "cap043-sheet-no-write");
        Check(!text.Contains("INSERT", StringComparison.Ordinal) && !text.Contains("ALTER", StringComparison.Ordinal), "cap043-sheet-no-script");

        var missingDates = ObjectPropertyCatalog.ParseObject(new RawRows(
            ["schema", "name", "type_desc", "create_date", "modify_date"],
            ["nvarchar", "nvarchar", "nvarchar", "datetime", "datetime"],
            [["dbo", "Orders", "USER_TABLE", null, DBNull.Value]],
            false));
        var missingText = ObjectPropertySheet.FormatCatalogObject(missingDates!);
        Check(missingText.Contains("not returned by the catalog", StringComparison.Ordinal), "cap043-dates-absent-are-named");
        Check(!missingText.Contains("0001-01-01", StringComparison.Ordinal), "cap043-dates-not-invented");

        Check(ObjectPropertyCatalog.ParseObject(new RawRows(["a"], ["x"], [], false)) is null, "cap043-empty-object-is-null");
        var none = ObjectPropertySheet.FormatNoRow("Orders");
        Check(none.Contains("no row", StringComparison.OrdinalIgnoreCase) && none.Contains("Nothing was changed", StringComparison.Ordinal), "cap043-no-row-honest");

        var dbFacts = ObjectPropertyCatalog.ParseDatabase(new RawRows(
            ["name", "create_date"], ["nvarchar", "datetime"], [["App", created]], false));
        var dbText = ObjectPropertySheet.FormatDatabase(dbFacts!);
        Check(dbText.Contains("App", StringComparison.Ordinal) && dbText.Contains("Database", StringComparison.Ordinal), "cap043-database-sheet");
        Check(dbText.Contains("2020-01-02 03:04:05.006", StringComparison.Ordinal), "cap043-database-created");
        Check(dbText.Contains("not provided by sys.databases", StringComparison.Ordinal), "cap043-database-modify-absent");

        var local = ObjectPropertySheet.FormatLocal("index", "IX_Orders", "dbo", null);
        Check(local.Contains("IX_Orders", StringComparison.Ordinal) && local.Contains("Index", StringComparison.Ordinal), "cap043-local-index");
        Check(local.Contains("not provided by the catalog for this item", StringComparison.Ordinal), "cap043-local-no-dates");
        Check(!ObjectPropertySheet.ReadsCatalog("index") && !ObjectPropertySheet.ReadsCatalog("column") &&
              !ObjectPropertySheet.ReadsCatalog("usertype") && !ObjectPropertySheet.ReadsCatalog("schema"), "cap043-skip-kinds-without-dates");
        Check(ObjectPropertySheet.ReadsCatalog("table") && ObjectPropertySheet.ReadsCatalog("database") &&
              ObjectPropertySheet.ReadsCatalog("procedure") && ObjectPropertySheet.ReadsCatalog("foreignkey"), "cap043-reads-object-kinds");

        var hostile = ObjectPropertySheet.FormatCatalogObject(new CatalogObjectFacts("d\nbo", "Or\rders", "USER_TABLE", created, modified));
        Check(!hostile.Contains('\n' + "bo", StringComparison.Ordinal) && !hostile.Contains("Or\rders", StringComparison.Ordinal), "cap043-hostile-name-one-line");
        Check(hostile.Split('\n').All(line => !line.Contains('\r')), "cap043-no-cr");
        Pass("cap043-object-properties");
    }

    static void UserDefinedTypes()
    {
        var sql = MetadataCatalog.ListUserDefinedTypes("My]Db");
        Check(sql.Contains("[My]]Db].sys.types", StringComparison.Ordinal), "cap034-sql-quotes-database");
        Check(sql.Contains("is_user_defined = 1", StringComparison.Ordinal), "cap034-sql-user-defined-only");
        Check(sql.Contains("is_user_defined = 0", StringComparison.Ordinal), "cap034-sql-system-type-join");
        Check(!sql.Contains("USE ", StringComparison.Ordinal), "cap034-sql-no-use");
        foreach (var word in new[] { "INSERT", "UPDATE", "DELETE", "ALTER", "DROP", "EXEC", "MERGE" })
            Check(!sql.Contains(word, StringComparison.OrdinalIgnoreCase), "cap034-sql-read-only-" + word);
        Throws<ArgumentException>(() => MetadataCatalog.ListUserDefinedTypes("bad\0"), "cap034-nul-database");

        Check(UserDefinedTypeCatalog.SystemTypeDisplay("nvarchar", 20, 0, 0, false) == "nvarchar(10)", "cap034-nvarchar-chars");
        Check(UserDefinedTypeCatalog.SystemTypeDisplay("nvarchar", -1, 0, 0, false) == "nvarchar(max)", "cap034-nvarchar-max");
        Check(UserDefinedTypeCatalog.SystemTypeDisplay("varchar", 10, 0, 0, false) == "varchar(10)", "cap034-varchar");
        Check(UserDefinedTypeCatalog.SystemTypeDisplay("decimal", 9, 18, 2, false) == "decimal(18,2)", "cap034-decimal");
        Check(UserDefinedTypeCatalog.SystemTypeDisplay("int", 4, 10, 0, false) == "int", "cap034-int");
        Check(UserDefinedTypeCatalog.SystemTypeDisplay("datetime2", 8, 0, 3, false) == "datetime2(3)", "cap034-datetime2");
        Check(UserDefinedTypeCatalog.SystemTypeDisplay(null, 0, 0, 0, true) == "table", "cap034-table-type");
        Check(UserDefinedTypeCatalog.SystemTypeDisplay(null, 0, 0, 0, false) == "not returned", "cap034-missing-system-type");

        var parsed = UserDefinedTypeCatalog.Parse(new RawRows(
            ["schema", "name", "base", "max_length", "precision", "scale", "is_table"],
            ["nvarchar", "nvarchar", "nvarchar", "smallint", "tinyint", "tinyint", "bit"],
            [
                ["dbo", "ZipCode", "nvarchar", (short)20, (byte)0, (byte)0, false],
                ["dbo", "Line\nItems", null, (short)0, (byte)0, (byte)0, true]
            ],
            false));
        Check(parsed.Count == 2 && parsed[0].SystemType == "nvarchar(10)" && !parsed[0].IsTableType, "cap034-parse-alias");
        Check(parsed[1].IsTableType && parsed[1].SystemType == "table", "cap034-parse-table-type");
        Check(UserDefinedTypeCatalog.Title(parsed[0]) == "dbo.ZipCode", "cap034-title");
        Check(!UserDefinedTypeCatalog.Title(parsed[1]).Contains('\n'), "cap034-title-one-line");
        Check(UserDefinedTypeCatalog.ExpandTitle(parsed[0]) == "System type · nvarchar(10)", "cap034-expand-system-type");
        Check(UserDefinedTypeCatalog.ExpandTitle(parsed[1]).Contains("table", StringComparison.Ordinal), "cap034-expand-table-type");
        var described = UserDefinedTypeCatalog.Describe(parsed[0]);
        Check(described.Contains("ZipCode", StringComparison.Ordinal) && described.Contains("nvarchar(10)", StringComparison.Ordinal), "cap034-describe");

        Check(ObjectExplorerService.DatabaseFolders.Any(f => f.FolderKind == "types" && f.ChildKind == "usertype" &&
            f.Title.Contains("User-defined", StringComparison.Ordinal)), "cap034-database-folder");
        Pass("cap034-user-defined-types");
    }

    static void StructuredValue()
    {
        Check(StructuredValueView.ExecutesContent == false, "cap096-does-not-execute");
        var xml = StructuredValueView.Present("xml", CellValue.Text("<root><child>a</child></root>", "string"));
        Check(xml.Shown && xml.Text.Contains("<child>a</child>", StringComparison.Ordinal) && xml.Text.Contains('\n'), "cap096-xml-pretty");
        Check(xml.Text.Contains(StructuredValueView.PolicyNote, StringComparison.Ordinal), "cap096-xml-policy");
        Check(!StructuredValueView.CanView("xml", CellValue.Null), "cap096-xml-null-not-offered");
        Check(StructuredValueView.CanView("xml", CellValue.Text("<a/>", "string")), "cap096-xml-offered");

        var json = StructuredValueView.Present("nvarchar", CellValue.Text("{\"b\":1,\"a\":[true,null]}", "string"));
        Check(json.Shown && json.Text.Contains("\"b\": 1", StringComparison.Ordinal) && json.Text.Contains('\n'), "cap096-json-pretty");
        Check(json.Text.Contains(StructuredValueView.PolicyNote, StringComparison.Ordinal), "cap096-json-policy");
        Check(StructuredValueView.CanView("nvarchar(max)", CellValue.Text("  [1, 2]  ", "string")), "cap096-json-array-and-length");
        Check(!StructuredValueView.CanView("nvarchar", CellValue.Text("hello", "string")), "cap096-nvarchar-text-not-json");
        Check(!StructuredValueView.CanView("varchar", CellValue.Text("{\"a\":1}", "string")), "cap096-varchar-not-offered");
        Check(!StructuredValueView.CanView("nchar", CellValue.Text("{", "string")), "cap096-nchar-not-offered");
        Check(!StructuredValueView.CanView(null, CellValue.Text("{\"a\":1}", "string")), "cap096-untyped-not-offered");

        var payload = StructuredValueView.Present("nvarchar", CellValue.Text("{\"sql\":\"SELECT 1\"}", "string"));
        Check(payload.Shown && payload.Text.Contains("SELECT 1", StringComparison.Ordinal) &&
              payload.Text.Contains(StructuredValueView.PolicyNote, StringComparison.Ordinal) &&
              !payload.Text.Contains("EXEC (", StringComparison.Ordinal), "cap096-json-content-not-executed");

        var dtd = StructuredValueView.Present("xml", CellValue.Text(
            "<!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><foo>&xxe;</foo>", "string"));
        Check(!dtd.Text.Contains("root:", StringComparison.Ordinal) && !dtd.Text.Contains("/bin/bash", StringComparison.Ordinal), "cap096-xml-dtd-not-expanded");
        Check(dtd.Text.Contains("not executed", StringComparison.OrdinalIgnoreCase), "cap096-xml-dtd-refused");

        var badJson = StructuredValueView.Present("nvarchar", CellValue.Text("{not json", "string"));
        Check(!badJson.Shown && badJson.Text.Contains("not executed", StringComparison.OrdinalIgnoreCase), "cap096-bad-json-refused");

        var truncated = StructuredValueView.Present("xml", CellValue.Text("<root/>", "string", truncated: true));
        Check(truncated.Text.Contains("truncated", StringComparison.OrdinalIgnoreCase) &&
              truncated.Text.Contains("does not re-query", StringComparison.OrdinalIgnoreCase), "cap096-truncated-no-requery");
        Pass("cap096-structured-value");
    }

    static void LongRunning()
    {
        var status = LongRunningStatus.Describe(1500);
        Check(status.StartsWith("Running", StringComparison.Ordinal), "cap078-status-running");
        Check(status.Contains("Client elapsed: 1500 ms", StringComparison.Ordinal), "cap078-status-elapsed");
        Check(status.Contains("not server statistics", StringComparison.Ordinal), "cap078-status-not-server");
        var messages = LongRunningStatus.FormatMessages(1500);
        Check(messages.Contains("Status: Running", StringComparison.Ordinal), "cap078-messages-running");
        Check(messages.Contains("Client elapsed: 1500 ms", StringComparison.Ordinal), "cap078-messages-elapsed");
        Check(!messages.Contains("Status: Succeeded", StringComparison.Ordinal), "cap078-messages-not-success");
        Check(!messages.Contains("Status: Failed", StringComparison.Ordinal), "cap078-messages-not-failed");
        Check(messages.Contains("has not finished", StringComparison.OrdinalIgnoreCase), "cap078-messages-unfinished");
        Throws<ArgumentOutOfRangeException>(() => LongRunningStatus.Describe(-1), "cap078-negative-elapsed");
        Check(LongRunningStatus.CancelControlVisible, "cap078-cancel-visible");
        Check(LongRunningStatus.CancelControlEnabled(true) && !LongRunningStatus.CancelControlEnabled(false), "cap078-cancel-enabled-only-while-running");
        Pass("cap078-long-running");
    }

    static void Wiring()
    {
        var appDir = Path.Combine(FindRepoRoot(), "src", "Sqloryn.App");
        var xaml = File.ReadAllText(Path.Combine(appDir, "MainWindow.axaml"));
        var shell = File.ReadAllText(Path.Combine(appDir, "MainWindow.Shell.cs"));
        var session = File.ReadAllText(Path.Combine(appDir, "MainWindow.Session.cs"));
        var results = File.ReadAllText(Path.Combine(appDir, "MainWindow.Results.cs"));
        var controller = File.ReadAllText(Path.Combine(appDir, "ExplorerController.cs"));
        var catalog = File.ReadAllText(Path.Combine(appDir, "CatalogClient.cs"));

        Check(xaml.Contains("Object properties", StringComparison.Ordinal), "cap043-properties-button");
        Check(xaml.Contains("Properties", StringComparison.Ordinal) && xaml.Contains("OnObjectProperties", StringComparison.Ordinal), "cap043-properties-click");
        Check(shell.Contains("Properties", StringComparison.Ordinal) && shell.Contains("OnObjectProperties", StringComparison.Ordinal), "cap043-properties-menu");
        Check(controller.Contains("LoadPropertiesAsync", StringComparison.Ordinal) &&
              controller.Contains("CatalogFailure.From", StringComparison.Ordinal), "cap043-denial-mapped");
        Check(catalog.Contains("ObjectFacts", StringComparison.Ordinal) && catalog.Contains("DatabaseFacts", StringComparison.Ordinal), "cap043-catalog-client");

        Check(controller.Contains("\"types\"", StringComparison.Ordinal) &&
              catalog.Contains("ListUserDefinedTypesAsync", StringComparison.Ordinal), "cap034-explorer-folder");
        Check(controller.Contains("UserDefinedTypeCatalog", StringComparison.Ordinal), "cap034-expand-uses-catalog");

        Check(xaml.Contains("View XML or JSON", StringComparison.Ordinal) && xaml.Contains("OnViewStructured", StringComparison.Ordinal), "cap096-view-button");
        Check(shell.Contains("OnViewStructured", StringComparison.Ordinal), "cap096-view-menu");
        Check(results.Contains("StructuredValueView.Present", StringComparison.Ordinal) &&
              results.Contains("does not re-query", StringComparison.OrdinalIgnoreCase), "cap096-results-wiring");

        Check(!xaml.Contains("Name=\"CancelButton\"", StringComparison.Ordinal) ||
              xaml.Contains("ToolTip.Tip=\"Cancel the running command", StringComparison.Ordinal), "cap078-cancel-tooltip");
        var cancel = xaml.Split('\n').First(line => line.Contains("Name=\"CancelButton\"", StringComparison.Ordinal));
        Check(!cancel.Contains("IsVisible=\"False\"", StringComparison.Ordinal), "cap078-cancel-not-hidden");
        Check(session.Contains("LongRunningStatus.Describe", StringComparison.Ordinal) &&
              session.Contains("LongRunningStatus.FormatMessages", StringComparison.Ordinal), "cap078-running-messages");
        var commandState = File.ReadAllText(Path.Combine(appDir, "MainWindow.axaml.cs"));
        Check(commandState.Contains("LongRunningStatus.CancelControlVisible", StringComparison.Ordinal) &&
              commandState.Contains("LongRunningStatus.CancelControlEnabled", StringComparison.Ordinal), "cap078-cancel-policy");
        Pass("cont-caps-wiring");
    }
}
