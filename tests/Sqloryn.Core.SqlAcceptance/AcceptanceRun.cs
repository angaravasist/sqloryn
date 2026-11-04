using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sqloryn.Core.SqlAcceptance;

sealed class AcceptanceFailure(string message) : Exception(message);

/// <summary>A case that cannot be exercised on this server; reported, never silently passed.</summary>
sealed class AcceptanceSkip(string reason) : Exception(reason);

/// <summary>
/// Drives Sqloryn.Core against the disposable instance. Every object lives in one uniquely named schema that is
/// dropped afterwards. Output lines: PASS/SKIP/FAIL CORE-*, then one CORE-ACCEPTANCE COMPLETE summary.
/// </summary>
sealed class AcceptanceRun
{
    readonly AcceptanceEnvironment _env;
    readonly SecretRedactor _redactor;
    readonly TextWriter _out;
    readonly string _schema = "acc_" + Guid.NewGuid().ToString("N")[..16];
    readonly List<string> _tables = [];
    int _pass, _skip, _fail;
    string _database = "";

    public AcceptanceRun(AcceptanceEnvironment env, SecretRedactor redactor, TextWriter output)
    {
        _env = env;
        _redactor = redactor;
        _out = output;
    }

    string Table(string name) => SqlIdentity.Qualify3(_database, _schema, name);

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        await using var admin = await ConnectAsync(ct);
        _database = admin.Database ?? _env.Database;
        _out.WriteLine($"Core acceptance target: SQL Server {admin.ServerVersion}; schema {_schema}");
        var ready = false;
        try
        {
            await ExecAsync(admin, $"CREATE SCHEMA {SqlIdentity.QuoteIdentifier(_schema)};", ct);
            ready = true;
        }
        catch (Exception error)
        {
            Report("CORE-SETUP", "FAIL", $"{error.GetType().Name}: {_redactor.Clean(error.Message)}");
        }

        try
        {
            if (ready)
            {
                await CheckAsync("CORE-MATERIALISE", "date/money/smallmoney materialise with NULL, negative, fractional, extremes",
                    () => MaterialiseAsync(admin, ct));
                await CheckAsync("CORE-EXPORT", "CSV and JSON export fidelity from the materialised result set",
                    () => ExportAsync(admin, ct));
                await CheckAsync("CORE-DESIGN-PK", "PreviewCreate DDL executes; second connection sees table and composite PK",
                    () => DesignPrimaryKeyAsync(admin, ct));
                await CheckAsync("CORE-EDIT-CONFLICT", "EditRows: CI-collation case change conflicts via BIN2; fresh edit commits; batch rolls back",
                    () => EditRowsAsync(admin, ct));
                await CheckAsync("CORE-PLAN-INSERT", "EstimatePlanAsync INSERT yields a plan and writes no row",
                    () => EstimatePlanAsync(admin, ct));
                await CheckAsync("CORE-PLAN-DENIED", "EstimatePlanAsync INSERT without SHOWPLAN is denied and writes no row",
                    () => EstimatePlanDeniedAsync(admin, ct));
            }
        }
        finally
        {
            await CleanupAsync(admin, ready);
        }

        _out.WriteLine($"CORE-ACCEPTANCE COMPLETE pass={_pass} skip={_skip} fail={_fail}");
        return _fail == 0 && ready ? 0 : 1;
    }

    // ---- check plumbing -------------------------------------------------------------------------------------

    async Task CheckAsync(string id, string title, Func<Task> body)
    {
        try
        {
            await body();
            Report(id, "PASS", title);
        }
        catch (AcceptanceSkip skip)
        {
            Report(id, "SKIP", _redactor.Clean(skip.Message));
        }
        catch (Exception error)
        {
            var detail = error is AcceptanceFailure ? error.Message : $"{error.GetType().Name}: {error.Message}";
            Report(id, "FAIL", _redactor.Clean(detail));
        }
    }

    void Report(string id, string kind, string text)
    {
        switch (kind)
        {
            case "PASS": _pass++; break;
            case "SKIP": _skip++; break;
            default: _fail++; break;
        }
        (kind == "FAIL" ? Console.Error : _out).WriteLine($"{kind} {id} {text}");
    }

    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new AcceptanceFailure(message);
    }

    static void RequireEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AcceptanceFailure($"{what}: expected '{expected}' but was '{actual}'.");
    }

    async Task<SqlSession> ConnectAsync(CancellationToken ct, string? user = null, string? password = null)
    {
        var session = new SqlSession();
        try
        {
            await session.ConnectAsync(_env.ToOptions(user, password), ct);
            if (_database.Length > 0 && !string.Equals(session.Database, _database, StringComparison.Ordinal))
                await session.ChangeDatabaseAsync(_database, ct);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    static async Task<QueryResult> ExecAsync(SqlSession session, string sql, CancellationToken ct)
    {
        var result = await session.ExecuteAsync(sql, ct);
        Require(result.Completion == QueryCompletionKind.Succeeded && result.TargetConfidence == TargetConfidence.Known,
            $"Statement did not succeed ({result.Completion}): {result.ErrorSummary}");
        return result;
    }

    static async Task<object?> ScalarAsync(SqlSession session, string sql, CancellationToken ct, params EditParameter[] parameters)
    {
        var rows = await session.QueryRawAsync(sql, parameters, 2, ct);
        Require(rows.Rows.Count == 1 && rows.Rows[0].Length == 1, "Scalar query did not return exactly one cell.");
        return rows.Rows[0][0];
    }

    // ---- 1. materialisation ---------------------------------------------------------------------------------

    sealed record Expected(int Id, string? Date, decimal? Money, decimal? Small, string? Text);

    static readonly Expected[] MaterialRows =
    [
        new(1, "2024-02-29", 1234.5678m, 214748.3647m, "a,b \"q\"\r\nx"),
        new(2, null, null, null, null),
        new(3, "0001-01-01", -922337203685477.5808m, -214748.3648m, ""),
        new(4, "9999-12-31", -0.0001m, 0.0001m, "  padded  "),
        new(5, "1999-12-31", 922337203685477.5807m, 0m, "=SUM(1)")
    ];

    const string MaterialSelect = "SELECT id, d, m, sm, s FROM {0} ORDER BY id;";

    async Task EnsureMaterialTableAsync(SqlSession session, CancellationToken ct)
    {
        if (_tables.Contains("t_mat"))
            return;
        var table = Table("t_mat");
        await ExecAsync(session,
            $"CREATE TABLE {table} (id int NOT NULL PRIMARY KEY, d date NULL, m money NULL, sm smallmoney NULL, s nvarchar(100) NULL);", ct);
        _tables.Add("t_mat");
        await ExecAsync(session, $"""
            INSERT INTO {table} (id, d, m, sm, s) VALUES
            (1, CONVERT(date, '2024-02-29', 23), CONVERT(money, 1234.5678), CONVERT(smallmoney, 214748.3647),
                N'a,b "q"' + NCHAR(13) + NCHAR(10) + N'x'),
            (2, NULL, NULL, NULL, NULL),
            (3, CONVERT(date, '0001-01-01', 23), CONVERT(money, -922337203685477.5808), CONVERT(smallmoney, -214748.3648), N''),
            (4, CONVERT(date, '9999-12-31', 23), CONVERT(money, -0.0001), CONVERT(smallmoney, 0.0001), N'  padded  '),
            (5, CONVERT(date, '1999-12-31', 23), CONVERT(money, 922337203685477.5807), CONVERT(smallmoney, 0), N'=SUM(1)');
            """, ct);
    }

    async Task<ResultSet> SelectMaterialAsync(SqlSession session, CancellationToken ct)
    {
        await EnsureMaterialTableAsync(session, ct);
        var result = await session.ExecuteAsync(string.Format(CultureInfo.InvariantCulture, MaterialSelect, Table("t_mat")), ct);
        Require(result.Completion == QueryCompletionKind.Succeeded,
            $"SELECT did not succeed ({result.Completion}): {result.ErrorSummary}");
        Require(result.ResultSets.Count == 1, $"Expected 1 result set, got {result.ResultSets.Count}.");
        var set = result.ResultSets[0];
        Require(!set.Truncated && !set.BudgetExceeded && !result.PartialResults && !result.RetentionLimitReached,
            "Result set was truncated or partial.");
        RequireEqual(MaterialRows.Length, set.Rows.Count, "row count");
        return set;
    }

    static void RequireDate(CellValue cell, string? expected, string what)
    {
        if (expected is null)
        {
            Require(cell.IsNull, $"{what}: expected NULL.");
            return;
        }
        Require(!cell.IsNull, $"{what}: unexpected NULL.");
        // Shipping path must emit ISO date only (yyyy-MM-dd), never a midnight datetime string.
        RequireEqual(expected, cell.Display, $"{what}: display");
        RequireEqual("date", cell.Kind, $"{what}: kind");
    }

    static void RequireMoney(CellValue cell, decimal? expected, string what, string expectedKind)
    {
        if (expected is null)
        {
            Require(cell.IsNull, $"{what}: expected NULL.");
            return;
        }
        Require(!cell.IsNull, $"{what}: unexpected NULL.");
        Require(cell.Display.IndexOfAny(['E', 'e', ',', ' ']) < 0, $"{what}: display '{cell.Display}' is not plain decimal text.");
        Require(decimal.TryParse(cell.Display, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var actual), $"{what}: '{cell.Display}' is not a decimal.");
        RequireEqual(expected.Value, actual, what + " value");
        // Four fractional digits from SqlMoney (e.g. 5.0000, -12.3456).
        Require(cell.Display.Contains('.', StringComparison.Ordinal) &&
                cell.Display[(cell.Display.IndexOf('.') + 1)..].Length == 4,
            $"{what}: expected exactly four fractional digits, got '{cell.Display}'.");
        RequireEqual(expectedKind, cell.Kind, $"{what}: kind");
    }

    async Task MaterialiseAsync(SqlSession session, CancellationToken ct)
    {
        var set = await SelectMaterialAsync(session, ct);
        Require(set.Columns.SequenceEqual(["id", "d", "m", "sm", "s"]), "column names differ.");
        RequireEqual("date", set.SqlTypes[1].ToLowerInvariant(), "date SQL type");
        RequireEqual("money", set.SqlTypes[2].ToLowerInvariant(), "money SQL type");
        RequireEqual("smallmoney", set.SqlTypes[3].ToLowerInvariant(), "smallmoney SQL type");
        for (var i = 0; i < MaterialRows.Length; i++)
        {
            var expected = MaterialRows[i];
            var row = set.Rows[i];
            RequireEqual(expected.Id.ToString(CultureInfo.InvariantCulture), row[0].Display, $"row {expected.Id} id");
            RequireDate(row[1], expected.Date, $"row {expected.Id} date");
            RequireMoney(row[2], expected.Money, $"row {expected.Id} money", "money");
            RequireMoney(row[3], expected.Small, $"row {expected.Id} smallmoney", "smallmoney");
            if (expected.Text is null)
                Require(row[4].IsNull, $"row {expected.Id} text: expected NULL.");
            else
                RequireEqual(expected.Text, row[4].Display, $"row {expected.Id} text");
        }
    }

    // ---- 2. export ------------------------------------------------------------------------------------------

    /// <summary>RFC 4180 reader; an unquoted empty field is null, a quoted empty field is "".</summary>
    static List<List<string?>> ParseCsv(string text)
    {
        var rows = new List<List<string?>>();
        var row = new List<string?>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        var wasQuoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; wasQuoted = true; break;
                case ',': row.Add(wasQuoted || field.Length > 0 ? field.ToString() : null); field.Clear(); wasQuoted = false; break;
                case '\r': break;
                case '\n':
                    row.Add(wasQuoted || field.Length > 0 ? field.ToString() : null);
                    rows.Add(row);
                    row = [];
                    field.Clear();
                    wasQuoted = false;
                    break;
                default: field.Append(ch); break;
            }
        }
        Require(!quoted, "CSV ended inside a quoted field.");
        if (field.Length > 0 || wasQuoted || row.Count > 0)
        {
            row.Add(wasQuoted || field.Length > 0 ? field.ToString() : null);
            rows.Add(row);
        }
        return rows;
    }

    static void RequireCsvMatches(ResultSetView view, string csv, bool spreadsheetSafe)
    {
        Require(csv.EndsWith("\r\n", StringComparison.Ordinal), "CSV must end with CRLF.");
        var parsed = ParseCsv(csv);
        RequireEqual(view.Rows.Count + 1, parsed.Count, "CSV record count");
        Require(parsed[0].SequenceEqual(view.Columns.Select(c => c.Name)), "CSV header differs.");
        for (var r = 0; r < view.Rows.Count; r++)
        {
            RequireEqual(view.Columns.Count, parsed[r + 1].Count, $"CSV row {r + 1} field count");
            for (var c = 0; c < view.Columns.Count; c++)
            {
                var cell = view.Rows[r][c];
                var actual = parsed[r + 1][c];
                if (cell.IsNull)
                {
                    Require(actual is null, $"CSV row {r + 1} col {c}: NULL must be an unquoted empty field.");
                    continue;
                }
                var expected = cell.Display;
                // Spreadsheet-safe mode may only prefix text cells that look like formulas; numbers/dates stay exact.
                if (spreadsheetSafe && c == 4 && expected.Length > 0 && expected[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
                    expected = "'" + expected;
                RequireEqual(expected, actual, $"CSV row {r + 1} col {c}");
            }
        }
    }

    static CellValue JsonCell(JsonElement element, string kind)
        => element.ValueKind == JsonValueKind.Null ? CellValue.Null : CellValue.Text(element.GetString()!, kind);

    async Task ExportAsync(SqlSession session, CancellationToken ct)
    {
        var set = await SelectMaterialAsync(session, ct);
        var view = ResultSetView.FromResultSet(set);
        Require(!view.Truncated, "Export view is marked truncated.");
        RequireEqual("money", view.Columns[2].SqlType.ToLowerInvariant(), "export money type");

        RequireCsvMatches(view, CsvExporter.ToCsv(view), spreadsheetSafe: false);
        RequireCsvMatches(view, CsvExporter.ToCsv(view, new CsvOptions { SpreadsheetSafe = true }), spreadsheetSafe: true);

        using var json = JsonDocument.Parse(JsonExporter.ToJson(view));
        var root = json.RootElement;
        RequireEqual(JsonExporter.FormatId, root.GetProperty("format").GetString(), "JSON format id");
        var sets = root.GetProperty("resultSets");
        RequireEqual(1, sets.GetArrayLength(), "JSON result set count");
        var jsonSet = sets[0];
        Require(!jsonSet.GetProperty("truncated").GetBoolean(), "JSON marked truncated.");
        var columns = jsonSet.GetProperty("columns");
        for (var c = 0; c < view.Columns.Count; c++)
        {
            RequireEqual(view.Columns[c].Name, columns[c].GetProperty("name").GetString(), $"JSON column {c} name");
            RequireEqual(view.Columns[c].SqlType, columns[c].GetProperty("sqlType").GetString(), $"JSON column {c} type");
        }
        var jsonRows = jsonSet.GetProperty("rows");
        RequireEqual(MaterialRows.Length, jsonRows.GetArrayLength(), "JSON row count");
        for (var r = 0; r < MaterialRows.Length; r++)
        {
            var expected = MaterialRows[r];
            var row = jsonRows[r];
            RequireEqual(expected.Id, row[0].GetInt32(), $"JSON row {r} id");
            for (var c = 1; c < view.Columns.Count; c++)
            {
                var cell = view.Rows[r][c];
                if (cell.IsNull)
                {
                    RequireEqual(JsonValueKind.Null, row[c].ValueKind, $"JSON row {r} col {c} kind");
                    continue;
                }
                // Money/date/text stay strings so no value passes through a binary double.
                RequireEqual(JsonValueKind.String, row[c].ValueKind, $"JSON row {r} col {c} kind");
                RequireEqual(cell.Display, row[c].GetString(), $"JSON row {r} col {c}");
            }
            // Full money/smallmoney precision survives as the exact decimal value, not a rounded double.
            RequireMoney(JsonCell(row[2], "money"), expected.Money, $"JSON row {r} money", "money");
            RequireMoney(JsonCell(row[3], "smallmoney"), expected.Small, $"JSON row {r} smallmoney", "smallmoney");
        }
    }

    // ---- 3. table design / primary key ----------------------------------------------------------------------

    async Task DesignPrimaryKeyAsync(SqlSession session, CancellationToken ct)
    {
        var draft = new TableDesignDraft
        {
            Database = _database,
            Schema = _schema,
            TableName = "t_design",
            Columns =
            [
                new ColumnDefinition { Name = "id", DataType = "int", IsNullable = false },
                new ColumnDefinition { Name = "code", DataType = "nvarchar", MaxLength = 20, IsNullable = false },
                new ColumnDefinition { Name = "label", DataType = "nvarchar", MaxLength = 50, IsNullable = true }
            ],
            PrimaryKeyColumns = ["id", "code"]
        };
        var preview = TableDesignSql.PreviewCreate(draft);
        Require(preview.HasChanges && preview.Warnings.Count == 0, "Preview must contain DDL and no warnings.");
        await ExecAsync(session, preview.ExecutableSql, ct);
        _tables.Add("t_design");

        await using var other = await ConnectAsync(ct);
        var columns = await other.ListColumnsAsync(_database, _schema, "t_design", ct);
        Require(columns.Select(c => c.Name).SequenceEqual(["id", "code", "label"]), "Second connection sees different columns.");
        Require(columns.Select(c => c.IsNullable).SequenceEqual([false, false, true]), "Nullability differs.");

        var key = await other.QueryRawAsync("""
            SELECT kc.name, c.name
            FROM sys.key_constraints AS kc
            INNER JOIN sys.index_columns AS ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE kc.parent_object_id = OBJECT_ID(@t) AND kc.type = 'PK'
            ORDER BY ic.key_ordinal;
            """, [new EditParameter("@t", Table("t_design"))], 10, ct);
        RequireEqual(2, key.Rows.Count, "primary key column count");
        RequireEqual("PK_t_design", (string?)key.Rows[0][0], "primary key name");
        Require(key.Rows.Select(r => (string?)r[1]).SequenceEqual(["id", "code"]), "Primary key column order differs.");

        // The key is enforced by the server: same (id, code) twice is rejected, a different code is accepted.
        var table = Table("t_design");
        await ExecAsync(session, $"INSERT INTO {table} (id, code) VALUES (1, N'a');", ct);
        var duplicate = await session.ExecuteAsync($"INSERT INTO {table} (id, code) VALUES (1, N'a');", ct);
        RequireEqual(QueryCompletionKind.Failed, duplicate.Completion, "duplicate key completion");
        await ExecAsync(session, $"INSERT INTO {table} (id, code) VALUES (1, N'b');", ct);
        RequireEqual(2, (int)(await ScalarAsync(other, $"SELECT COUNT(*) FROM {table};", ct))!, "design row count");
    }

    // ---- 4. EditRows ----------------------------------------------------------------------------------------

    static IReadOnlyDictionary<string, object?> Snapshot(RawRows rows, int index)
        => rows.Columns.Select((name, i) => (name, value: rows.Rows[index][i]))
            .ToDictionary(p => p.name, p => p.value, StringComparer.Ordinal);

    async Task EditRowsAsync(SqlSession editor, CancellationToken ct)
    {
        var table = Table("t_edit");
        var tableRef = new TableRef(_database, _schema, "t_edit");
        await ExecAsync(editor, $"""
            CREATE TABLE {table} (
                id int NOT NULL PRIMARY KEY,
                label nvarchar(50) COLLATE Latin1_General_CI_AS NOT NULL,
                note nvarchar(50) COLLATE Latin1_General_CI_AS NULL,
                qty int NULL);
            """, ct);
        _tables.Add("t_edit");
        await ExecAsync(editor, $"INSERT INTO {table} (id, label, note, qty) VALUES (1, N'Alpha', NULL, 10), (2, N'Beta', N'b', 20);", ct);

        await using var other = await ConnectAsync(ct);
        var characterColumns = new HashSet<string>(StringComparer.Ordinal) { "label", "note" };
        var select = $"SELECT id, label, note, qty FROM {table} ORDER BY id;";
        var key1 = new RowKey(["id"], [1]);

        // 1. Editor reads a baseline; a second session then changes only the case of row 1's label.
        var baseline = await editor.QueryRawAsync(select, null, 10, ct);
        RequireEqual("Alpha", (string?)baseline.Rows[0][1], "baseline label");
        await ExecAsync(other, $"UPDATE {table} SET label = N'ALPHA' WHERE id = 1;", ct);
        // Precondition: under the column's CI collation a plain equality still matches, so only BIN2 can see the change.
        RequireEqual(1, (int)(await ScalarAsync(other, $"SELECT COUNT(*) FROM {table} WHERE id = 1 AND label = N'Alpha';", ct))!,
            "CI collation precondition");

        var stale = EditRowsSql.BuildUpdate(tableRef, key1, new Dictionary<string, object?> { ["qty"] = 11 },
            Snapshot(baseline, 0), characterColumns: characterColumns);
        Require(stale.CommandText.Contains(EditRowsSql.CharacterConcurrencyCollation, StringComparison.Ordinal),
            "Concurrency predicate does not use the BIN2 collation.");
        var conflict = await editor.ApplyEditsAsync([stale], ct);
        RequireEqual(EditBatchOutcome.RolledBackConflict, conflict.Outcome, "case-change conflict outcome");
        RequireEqual(0, conflict.FailedIndex ?? -1, "case-change failed index");
        RequireEqual(10, (int)(await ScalarAsync(other, $"SELECT qty FROM {table} WHERE id = 1;", ct))!, "row 1 qty after conflict");
        RequireEqual(1, (int)(await ScalarAsync(other,
            $"SELECT COUNT(*) FROM {table} WHERE id = 1 AND label COLLATE Latin1_General_BIN2 = N'ALPHA';", ct))!, "row 1 label after conflict");
        Require(editor.IsConnected, "Editor session dropped after a clean rollback.");

        // 2. Fresh baseline: the same BIN2 predicate must not raise a false conflict.
        var fresh = await editor.QueryRawAsync(select, null, 10, ct);
        RequireEqual("ALPHA", (string?)fresh.Rows[0][1], "fresh label");
        var good = EditRowsSql.BuildUpdate(tableRef, key1, new Dictionary<string, object?> { ["qty"] = 12 },
            Snapshot(fresh, 0), characterColumns: characterColumns);
        var committed = await editor.ApplyEditsAsync([good], ct);
        RequireEqual(EditBatchOutcome.Committed, committed.Outcome, "fresh edit outcome");
        RequireEqual(12, (int)(await ScalarAsync(other, $"SELECT qty FROM {table} WHERE id = 1;", ct))!, "row 1 qty after commit");

        // 3. Two-row batch where the second write conflicts: the first write must roll back with it.
        var batchBase = await editor.QueryRawAsync(select, null, 10, ct);
        await ExecAsync(other, $"UPDATE {table} SET note = N'B' WHERE id = 2;", ct);
        var first = EditRowsSql.BuildUpdate(tableRef, key1, new Dictionary<string, object?> { ["qty"] = 99 },
            Snapshot(batchBase, 0), characterColumns: characterColumns);
        var second = EditRowsSql.BuildUpdate(tableRef, new RowKey(["id"], [2]), new Dictionary<string, object?> { ["qty"] = 98 },
            Snapshot(batchBase, 1), characterColumns: characterColumns);
        var batch = await editor.ApplyEditsAsync([first, second], ct);
        RequireEqual(EditBatchOutcome.RolledBackConflict, batch.Outcome, "batch outcome");
        RequireEqual(1, batch.FailedIndex ?? -1, "batch failed index");
        RequireEqual(12, (int)(await ScalarAsync(other, $"SELECT qty FROM {table} WHERE id = 1;", ct))!, "row 1 qty after batch rollback");
        RequireEqual(20, (int)(await ScalarAsync(other, $"SELECT qty FROM {table} WHERE id = 2;", ct))!, "row 2 qty after batch rollback");
    }

    // ---- 5. estimated plan ----------------------------------------------------------------------------------

    async Task EnsurePlanTableAsync(SqlSession session, CancellationToken ct)
    {
        if (_tables.Contains("t_plan"))
            return;
        await ExecAsync(session, $"CREATE TABLE {Table("t_plan")} (id int NOT NULL PRIMARY KEY, v nvarchar(20) NULL);", ct);
        _tables.Add("t_plan");
    }

    string PlanInsert => $"INSERT INTO {Table("t_plan")} (id, v) VALUES (1, N'x');";

    async Task<int> PlanRowCountAsync(SqlSession verifier, CancellationToken ct)
        => (int)(await ScalarAsync(verifier, $"SELECT COUNT(*) FROM {Table("t_plan")};", ct))!;

    async Task EstimatePlanAsync(SqlSession session, CancellationToken ct)
    {
        await EnsurePlanTableAsync(session, ct);
        await using var verifier = await ConnectAsync(ct);
        RequireEqual(0, await PlanRowCountAsync(verifier, ct), "rows before estimate");
        var plan = await session.EstimatePlanAsync(PlanInsert, ct);
        RequireEqual(EstimatedPlanStatus.Succeeded, plan.Status, "estimate status (" + plan.Error + ")");
        Require(!plan.SessionTainted && plan.PlanXml.Count >= 1 &&
                plan.PlanXml.All(xml => xml.Contains("ShowPlanXML", StringComparison.Ordinal)),
            "Estimate did not return plan XML.");
        RequireEqual(0, await PlanRowCountAsync(verifier, ct), "rows after estimate (INSERT must not execute)");
        // SHOWPLAN must be OFF again: a normal batch returns data, not a plan document.
        var after = await ExecAsync(session, "SELECT 1 AS one;", ct);
        RequireEqual("1", after.ResultSets[0].Rows[0][0].Display, "post-estimate SELECT");
        Require(session.IsConnected, "Session closed after a successful estimate.");
    }

    async Task EstimatePlanDeniedAsync(SqlSession admin, CancellationToken ct)
    {
        await EnsurePlanTableAsync(admin, ct);
        var name = "acc_deny_" + Guid.NewGuid().ToString("N")[..12];
        var password = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _redactor.Add(password);
        var login = SqlIdentity.QuoteIdentifier(name);
        var loginCreated = false;
        var userCreated = false;
        SqlSession? denied = null;
        try
        {
            try
            {
                await ExecAsync(admin, $"CREATE LOGIN {login} WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;", ct);
                loginCreated = true;
                await ExecAsync(admin, $"CREATE USER {login} FOR LOGIN {login};", ct);
                userCreated = true;
            }
            catch (AcceptanceFailure error) when (!loginCreated || !userCreated)
            {
                throw new AcceptanceSkip("cannot create a restricted login on this server: " + error.Message);
            }

            var schema = SqlIdentity.QuoteIdentifier(_schema);
            await ExecAsync(admin, $"GRANT SELECT, INSERT ON SCHEMA::{schema} TO {login};", ct);
            await ExecAsync(admin, $"DENY SHOWPLAN TO {login};", ct);

            denied = await ConnectAsync(ct, name, password);
            await using var verifier = await ConnectAsync(ct);
            RequireEqual(0, await PlanRowCountAsync(verifier, ct), "rows before denied estimate");
            var plan = await denied.EstimatePlanAsync(PlanInsert, ct);
            RequireEqual(EstimatedPlanStatus.PermissionDenied, plan.Status, "denied estimate status (" + plan.Error + ")");
            Require(plan.PlanXml.Count == 0, "A denied estimate returned plan XML.");
            RequireEqual(0, await PlanRowCountAsync(verifier, ct), "rows after denied estimate (INSERT must not execute)");
        }
        finally
        {
            if (denied is not null)
                await denied.DisposeAsync();
            if (userCreated)
                await TryAsync(admin, $"DROP USER IF EXISTS {login};");
            if (loginCreated)
                await TryAsync(admin, $"DROP LOGIN {login};");
        }
    }

    async Task TryAsync(SqlSession session, string sql)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ExecAsync(session, sql, cleanup.Token);
        }
        catch (Exception error)
        {
            Report("CORE-CLEANUP", "FAIL", _redactor.Clean($"{sql[..Math.Min(40, sql.Length)]}… {error.Message}"));
        }
    }

    async Task CleanupAsync(SqlSession admin, bool schemaCreated)
    {
        if (!schemaCreated)
            return;
        foreach (var table in _tables.AsEnumerable().Reverse())
            await TryAsync(admin, $"DROP TABLE IF EXISTS {Table(table)};");
        await TryAsync(admin, $"DROP SCHEMA IF EXISTS {SqlIdentity.QuoteIdentifier(_schema)};");
    }
}
