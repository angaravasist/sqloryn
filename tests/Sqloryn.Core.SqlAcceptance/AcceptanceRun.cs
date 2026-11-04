using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    readonly List<string> _users = [];
    readonly List<string> _databases = [];
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
                await CheckAsync("CORE-TIME-GUID", "time and uniqueidentifier materialise from a second session",
                    () => TimeGuidAsync(admin, ct));
                await CheckAsync("CORE-DESIGN-APPLY", "ApplyTableDesignAsync creates, adds a column, rolls a failed batch back, and refuses a stale fingerprint",
                    () => DesignApplyAsync(admin, ct));
                await CheckAsync("CORE-EDIT-INSERT-DELETE", "EditRows insert and delete: conflict rolls back; a fresh delete commits; second session verifies",
                    () => EditInsertDeleteAsync(admin, ct));
                await CheckAsync("CORE-CSV-IMPORT", "CSV Create and Append store exact values, including rows past the preview sample",
                    () => CsvImportAsync(admin, ct));
                await CheckAsync("CORE-SECURITY", "GRANT preview is not executed; a second session sees the same permissions",
                    () => SecurityPreviewAsync(admin, ct));
                await CheckAsync("CORE-BACKUP", "COPY_ONLY backup of a disposable database is recorded in msdb, or the server refuses it",
                    () => BackupAsync(admin, ct));
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

    // ---- 6. time + uniqueidentifier --------------------------------------------------------------------------

    async Task TimeGuidAsync(SqlSession session, CancellationToken ct)
    {
        var table = Table("t_tg");
        await ExecAsync(session, $"CREATE TABLE {table} (id int NOT NULL PRIMARY KEY, t time(7) NULL, g uniqueidentifier NULL);", ct);
        _tables.Add("t_tg");
        await ExecAsync(session, $"""
            INSERT INTO {table} (id, t, g) VALUES
            (1, CAST('13:45:30' AS time(7)), CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier)),
            (2, CAST('00:00:00.1234567' AS time(7)), CAST('00000000-0000-0000-0000-000000000000' AS uniqueidentifier)),
            (3, NULL, NULL),
            (4, CAST('23:59:59.9999999' AS time(7)), CAST('FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF' AS uniqueidentifier)),
            (5, CAST('01:02:03.4500000' AS time(7)), CAST('AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE' AS uniqueidentifier));
            """, ct);

        await using var other = await ConnectAsync(ct);
        var result = await other.ExecuteAsync($"SELECT id, t, g FROM {table} ORDER BY id;", ct);
        Require(result.Completion == QueryCompletionKind.Succeeded, $"SELECT did not succeed ({result.Completion}): {result.ErrorSummary}");
        RequireEqual(1, result.ResultSets.Count, "time/guid result sets");
        var set = result.ResultSets[0];
        Require(!set.Truncated && !set.BudgetExceeded && !result.PartialResults, "time/guid result was truncated.");
        RequireEqual(5, set.Rows.Count, "time/guid row count");
        RequireEqual("time", set.SqlTypes[1].ToLowerInvariant(), "time SQL type");
        RequireEqual("uniqueidentifier", set.SqlTypes[2].ToLowerInvariant(), "uniqueidentifier SQL type");

        void Cell(int row, int column, string? display, string kind)
        {
            var cell = set.Rows[row][column];
            if (display is null)
            {
                Require(cell.IsNull, $"row {row + 1} col {column}: expected NULL.");
                return;
            }
            Require(!cell.IsNull, $"row {row + 1} col {column}: unexpected NULL.");
            RequireEqual(kind, cell.Kind, $"row {row + 1} col {column} kind");
            RequireEqual(display, cell.Display, $"row {row + 1} col {column} display");
        }

        Cell(0, 0, "1", set.Rows[0][0].Kind);
        Cell(0, 1, "13:45:30", "time");
        Cell(0, 2, "6f9619ff-8b86-d011-b42d-00c04fc964ff", "uniqueidentifier");
        Cell(1, 1, "00:00:00.1234567", "time");
        Cell(1, 2, "00000000-0000-0000-0000-000000000000", "uniqueidentifier");
        Cell(2, 1, null, "time");
        Cell(2, 2, null, "uniqueidentifier");
        Cell(3, 1, "23:59:59.9999999", "time");
        Cell(3, 2, "ffffffff-ffff-ffff-ffff-ffffffffffff", "uniqueidentifier");
        Cell(4, 1, "01:02:03.45", "time");
        Cell(4, 2, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "uniqueidentifier");
    }

    // ---- 7. transactional table design ----------------------------------------------------------------------

    TableDesignDraft ApplyDraft(string table, params ColumnDefinition[] columns) => new()
    {
        Database = _database,
        Schema = _schema,
        TableName = table,
        Columns = columns,
        PrimaryKeyColumns = ["id"]
    };

    static ColumnDefinition Col(string name, string type, bool nullable, int? length = null)
        => new() { Name = name, DataType = type, IsNullable = nullable, MaxLength = length };

    async Task DesignApplyAsync(SqlSession session, CancellationToken ct)
    {
        const string name = "t_apply";
        var tableRef = new TableRef(_database, _schema, name);
        var created = ApplyDraft(name,
            Col("id", "int", nullable: false),
            Col("code", "nvarchar", nullable: false, length: 20));
        var create = TableDesignSql.PreviewCreate(created);
        Require(create.Statements.Count == 1 && create.Statements[0].Sql.StartsWith("CREATE TABLE ", StringComparison.Ordinal),
            "Create preview is not one CREATE TABLE statement.");
        var applied = await session.ApplyTableDesignAsync(tableRef, create.Statements, TableDesignFingerprint.Absent, ct);
        RequireEqual(TableDesignApplyOutcome.Committed, applied.Outcome, "create outcome (" + applied.Message + ")");
        _tables.Add(name);

        await using var other = await ConnectAsync(ct);
        var columns = await other.ListColumnsAsync(_database, _schema, name, ct);
        Require(columns.Select(c => c.Name).SequenceEqual(["id", "code"]), "Second connection does not see the created columns.");
        await ExecAsync(session, $"INSERT INTO {Table(name)} (id, code) VALUES (1, N'keep');", ct);

        var withNote = ApplyDraft(name,
            Col("id", "int", nullable: false),
            Col("code", "nvarchar", nullable: false, length: 20),
            Col("note", "nvarchar", nullable: true, length: 40));
        var fingerprint = await session.ReadTableFingerprintAsync(tableRef, ct);
        var additive = TableDesignSql.PreviewAlter(created, withNote);
        Require(additive.Statements.Count == 1 && additive.Statements[0].Sql.Contains(" ADD ", StringComparison.Ordinal),
            "Additive preview did not emit a single ADD.");
        var added = await session.ApplyTableDesignAsync(tableRef, additive.Statements, fingerprint, ct);
        RequireEqual(TableDesignApplyOutcome.Committed, added.Outcome, "additive outcome (" + added.Message + ")");
        columns = await other.ListColumnsAsync(_database, _schema, name, ct);
        Require(columns.Select(c => c.Name).SequenceEqual(["id", "code", "note"]), "Second connection does not see the added column.");
        RequireEqual("keep", (string?)(await ScalarAsync(other, $"SELECT code FROM {Table(name)} WHERE id = 1;", ct))!,
            "row survived the additive alter");

        var breaking = ApplyDraft(name,
            Col("id", "int", nullable: false),
            Col("extra", "int", nullable: true),
            Col("code", "int", nullable: false),
            Col("note", "nvarchar", nullable: true, length: 40));
        var batchPreview = TableDesignSql.PreviewAlter(withNote, breaking);
        Require(batchPreview.Statements.Count >= 2, "Rollback batch did not contain two statements.");
        Require(batchPreview.Statements[0].Sql.Contains(" ADD ", StringComparison.Ordinal), "First batch statement is not an ADD.");
        var fresh = await session.ReadTableFingerprintAsync(tableRef, ct);
        var rolled = await session.ApplyTableDesignAsync(tableRef, batchPreview.Statements, fresh, ct);
        RequireEqual(TableDesignApplyOutcome.RolledBackError, rolled.Outcome, "batch outcome (" + rolled.Message + ")");
        RequireEqual(1, rolled.StatementsExecuted, "statements completed before the failing alter");
        RequireEqual(1, rolled.FailedIndex ?? -1, "failing statement index");
        columns = await other.ListColumnsAsync(_database, _schema, name, ct);
        Require(columns.Select(c => c.Name).SequenceEqual(["id", "code", "note"]), "Rolled-back ADD is still visible.");
        Require(columns.Any(c => c.Name == "code" && c.TypeName.Equals("nvarchar", StringComparison.OrdinalIgnoreCase)),
            "code column type changed despite rollback.");
        RequireEqual("keep", (string?)(await ScalarAsync(other, $"SELECT code FROM {Table(name)} WHERE id = 1;", ct))!,
            "row after rolled-back batch");

        var staleFingerprint = await session.ReadTableFingerprintAsync(tableRef, ct);
        await ExecAsync(other, $"ALTER TABLE {Table(name)} ADD stale_probe int NULL;", ct);
        var ghost = TableDesignSql.PreviewAlter(withNote, ApplyDraft(name,
            Col("id", "int", nullable: false),
            Col("code", "nvarchar", nullable: false, length: 20),
            Col("note", "nvarchar", nullable: true, length: 40),
            Col("ghost", "int", nullable: true)));
        var refused = await session.ApplyTableDesignAsync(tableRef, ghost.Statements, staleFingerprint, ct);
        RequireEqual(TableDesignApplyOutcome.RefusedStale, refused.Outcome, "stale outcome (" + refused.Message + ")");
        RequireEqual(0, refused.StatementsExecuted, "stale apply executed a statement");
        columns = await other.ListColumnsAsync(_database, _schema, name, ct);
        Require(columns.Any(c => c.Name == "stale_probe"), "The interfering column is missing, so the fingerprint may not have changed.");
        Require(!columns.Any(c => c.Name == "ghost"), "A stale design still added a column.");
        RequireEqual("keep", (string?)(await ScalarAsync(other, $"SELECT code FROM {Table(name)} WHERE id = 1;", ct))!,
            "row after refused stale apply");
        Require(session.IsConnected, "Design session dropped after a refused apply.");
    }

    // ---- 8. insert / delete ---------------------------------------------------------------------------------

    async Task EditInsertDeleteAsync(SqlSession editor, CancellationToken ct)
    {
        const string name = "t_row";
        var table = Table(name);
        var tableRef = new TableRef(_database, _schema, name);
        await ExecAsync(editor, $"""
            CREATE TABLE {table} (
                id int NOT NULL PRIMARY KEY,
                label nvarchar(50) COLLATE Latin1_General_CI_AS NOT NULL,
                qty int NULL);
            """, ct);
        _tables.Add(name);
        var characterColumns = new HashSet<string>(StringComparer.Ordinal) { "label" };
        var select = $"SELECT id, label, qty FROM {table} ORDER BY id;";

        async Task<EditBatchResult> ApplyAsync(params EditCommand[] commands) => await editor.ApplyEditsAsync(commands, ct);

        var inserted = await ApplyAsync(EditRowsSql.BuildInsert(tableRef, new Dictionary<string, object?>
        {
            ["id"] = 1, ["label"] = "Alpha", ["qty"] = 10
        }));
        RequireEqual(EditBatchOutcome.Committed, inserted.Outcome, "insert outcome (" + inserted.Message + ")");
        var second = await ApplyAsync(EditRowsSql.BuildInsert(tableRef, new Dictionary<string, object?>
        {
            ["id"] = 2, ["label"] = "Beta", ["qty"] = 20
        }));
        RequireEqual(EditBatchOutcome.Committed, second.Outcome, "second insert outcome (" + second.Message + ")");

        await using var other = await ConnectAsync(ct);
        RequireEqual(2, (int)(await ScalarAsync(other, $"SELECT COUNT(*) FROM {table};", ct))!, "rows after inserts");
        RequireEqual("Alpha", (string?)(await ScalarAsync(other, $"SELECT label FROM {table} WHERE id = 1;", ct))!, "inserted label");

        var baseline = await editor.QueryRawAsync(select, null, 10, ct);
        await ExecAsync(other, $"UPDATE {table} SET label = N'ALPHA' WHERE id = 1;", ct);
        var staleDelete = await ApplyAsync(EditRowsSql.BuildDelete(
            tableRef, new RowKey(["id"], [1]), Snapshot(baseline, 0), characterColumns: characterColumns));
        RequireEqual(EditBatchOutcome.RolledBackConflict, staleDelete.Outcome, "stale delete outcome (" + staleDelete.Message + ")");
        RequireEqual(1, (int)(await ScalarAsync(other,
            $"SELECT COUNT(*) FROM {table} WHERE id = 1 AND label COLLATE Latin1_General_BIN2 = N'ALPHA' AND qty = 10;", ct))!,
            "row 1 after conflicting delete");

        var beforeBatch = await editor.QueryRawAsync(select, null, 10, ct);
        var row2 = beforeBatch.Rows.Select((row, index) => (row, index)).Single(p => (int)p.row[0]! == 2).index;
        await ExecAsync(other, $"UPDATE {table} SET qty = 21 WHERE id = 2;", ct);
        var batch = await ApplyAsync(
            EditRowsSql.BuildInsert(tableRef, new Dictionary<string, object?> { ["id"] = 3, ["label"] = "Gamma", ["qty"] = 30 }),
            EditRowsSql.BuildDelete(tableRef, new RowKey(["id"], [2]), Snapshot(beforeBatch, row2), characterColumns: characterColumns));
        RequireEqual(EditBatchOutcome.RolledBackConflict, batch.Outcome, "insert+delete batch outcome (" + batch.Message + ")");
        RequireEqual(1, batch.FailedIndex ?? -1, "insert+delete failed index");
        RequireEqual(0, (int)(await ScalarAsync(other, $"SELECT COUNT(*) FROM {table} WHERE id = 3;", ct))!, "rolled-back insert is visible");
        RequireEqual(21, (int)(await ScalarAsync(other, $"SELECT qty FROM {table} WHERE id = 2;", ct))!, "row 2 after conflicting batch");

        var current = await editor.QueryRawAsync(select, null, 10, ct);
        var current2 = current.Rows.Select((row, index) => (row, index)).Single(p => (int)p.row[0]! == 2).index;
        var removed = await ApplyAsync(EditRowsSql.BuildDelete(
            tableRef, new RowKey(["id"], [2]), Snapshot(current, current2), characterColumns: characterColumns));
        RequireEqual(EditBatchOutcome.Committed, removed.Outcome, "fresh delete outcome (" + removed.Message + ")");
        RequireEqual(0, (int)(await ScalarAsync(other, $"SELECT COUNT(*) FROM {table} WHERE id = 2;", ct))!, "row 2 after fresh delete");
        RequireEqual(1, (int)(await ScalarAsync(other, $"SELECT COUNT(*) FROM {table} WHERE id = 1;", ct))!, "row 1 remains");

        var duplicate = await ApplyAsync(EditRowsSql.BuildInsert(tableRef, new Dictionary<string, object?>
        {
            ["id"] = 1, ["label"] = "Other", ["qty"] = 99
        }));
        RequireEqual(EditBatchOutcome.RolledBackError, duplicate.Outcome, "duplicate insert outcome (" + duplicate.Message + ")");
        RequireEqual(1, (int)(await ScalarAsync(other, $"SELECT COUNT(*) FROM {table} WHERE id = 1 AND qty = 10;", ct))!,
            "duplicate insert changed the existing row");
        Require(editor.IsConnected, "Editor session dropped after a rolled-back insert.");
    }

    // ---- 9. CSV import --------------------------------------------------------------------------------------

    async Task CsvImportAsync(SqlSession session, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "sqloryn-acc-" + Guid.NewGuid().ToString("N") + ".csv");
        const string beyond = "beyond-sample";
        var csv = "id,label,amount,when_utc,token\n"
            + "1,alpha,10.50,2024-02-29T13:45:30.1234567,6f9619ff-8b86-d011-b42d-00c04fc964ff\n"
            + "2,\"a,b \"\"q\"\"\",-0.0001,1999-12-31,00000000-0000-0000-0000-000000000000\n"
            + "3," + beyond + ",1234567890.123456789,2020-01-02T00:00:00.0000000,11111111-2222-3333-4444-555555555555\n";
        await File.WriteAllTextAsync(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
        try
        {
            var options = new CsvImportOptions { HasHeader = true, SampleRowCount = 1, ScanRowLimit = 100 };
            var preview = CsvImportPreview.Parse(path, options);
            Require(!preview.HasErrors, "CSV preview reported errors.");
            RequireEqual(1, preview.SampleRows.Count, "preview sample row count");
            Require(!preview.SampleRows.Any(row => row.Any(cell => cell.Contains(beyond, StringComparison.Ordinal))),
                "The preview sample includes the tail row.");
            var amount = preview.Columns.Single(c => c.Name == "amount");
            RequireEqual(CsvSqlType.Decimal, amount.GuessedType, "amount guessed type");
            RequireEqual(9, amount.MaxScale, "amount scale must come from the row past the sample");

            const string createdName = "t_csv_new";
            var createdRef = new TableRef(_database, _schema, createdName);
            var createPlan = CsvImportPlan.FromPreview(preview, createdRef, CsvImportMode.Create);
            var amountMap = createPlan.Mappings.Single(m => m.TargetColumn == "amount");
            RequireEqual(CsvSqlType.Decimal, amountMap.Type, "create amount type");
            RequireEqual(9, amountMap.Scale ?? -1, "create amount scale");
            _tables.Add(createdName);
            var createSource = CsvImportSource.LoadImmutable(path, options);
            var created = await session.ApplyCsvImportAsync(createPlan, createSource, null, ct);
            RequireEqual(CsvImportOutcome.Committed, created.Outcome, "create import outcome (" + created.Message + ")");
            RequireEqual(3L, created.RowsInserted, "create rows inserted");
            // CREATE DDL must keep scale 9 (never clamp to 18); second session reads exact text.
            Require(createPlan.GetPrepareStatements()[0].Contains("decimal(38, 9)", StringComparison.Ordinal),
                "CREATE DDL must emit decimal(38, 9) for the observed scale");

            await using var other = await ConnectAsync(ct);
            await RequireCsvRowsAsync(other, Table(createdName), includeSentinel: false, ct);

            const string appendName = "t_csv_app";
            var appendRef = new TableRef(_database, _schema, appendName);
            var ddl = CsvImportPlan.FromPreview(preview, appendRef, CsvImportMode.Create);
            await ExecAsync(session, ddl.GetPrepareStatements()[0], ct);
            _tables.Add(appendName);
            await ExecAsync(session, $"""
                INSERT INTO {Table(appendName)} (id, label, amount, when_utc, token) VALUES
                (9, N'sentinel-keep', CONVERT(decimal(38, 9), '7.125'), CONVERT(datetime2(7), '2019-03-04T05:06:07.0000001', 126),
                 CONVERT(uniqueidentifier, 'AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'));
                """, ct);
            // Append carries destination metadata so lossy cells are refused before any write.
            var appendMappings = preview.Columns.Select(c =>
            {
                var guessed = c.GuessedType;
                CsvDestinationType? dest = guessed switch
                {
                    CsvSqlType.Decimal => new CsvDestinationType("decimal", Precision: 38, Scale: c.MaxScale),
                    CsvSqlType.NVarChar => new CsvDestinationType("nvarchar", MaxLength: 4000),
                    CsvSqlType.DateTime2 => new CsvDestinationType("datetime2", Scale: 7),
                    CsvSqlType.UniqueIdentifier => new CsvDestinationType("uniqueidentifier"),
                    CsvSqlType.Int => new CsvDestinationType("int"),
                    _ => new CsvDestinationType("nvarchar", MaxLength: 4000)
                };
                return new CsvColumnMapping(c.Index, c.Name, guessed,
                    guessed == CsvSqlType.NVarChar ? CsvImportPlan.SuggestTextLength(c.MaxLength, preview.ScanTruncated) : null,
                    guessed == CsvSqlType.Decimal ? c.MaxScale : null,
                    dest);
            }).ToList();
            var appendPlan = new CsvImportPlan(appendRef, CsvImportMode.Append, appendMappings, null, false);
            var appendSource = CsvImportSource.LoadImmutable(path, options);
            var appended = await session.ApplyCsvImportAsync(appendPlan, appendSource, null, ct);
            RequireEqual(CsvImportOutcome.Committed, appended.Outcome, "append import outcome (" + appended.Message + ")");
            RequireEqual(3L, appended.RowsInserted, "append rows inserted");
            await RequireCsvRowsAsync(other, Table(appendName), includeSentinel: true, ct);

            // Same row count, different content: fingerprint refuse; Replace must leave the sentinel.
            const string replaceName = "t_csv_rep";
            var replaceRef = new TableRef(_database, _schema, replaceName);
            var replaceDdl = CsvImportPlan.FromPreview(preview, replaceRef, CsvImportMode.Create);
            await ExecAsync(session, replaceDdl.GetPrepareStatements()[0], ct);
            _tables.Add(replaceName);
            await ExecAsync(session, $"""
                INSERT INTO {Table(replaceName)} (id, label, amount, when_utc, token) VALUES
                (9, N'sentinel-keep', CONVERT(decimal(38, 9), '7.125'), CONVERT(datetime2(7), '2019-03-04T05:06:07.0000001', 126),
                 CONVERT(uniqueidentifier, 'AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'));
                """, ct);
            var replacePath = path;
            var replaceSource = CsvImportSource.LoadImmutable(replacePath, options);
            // Mutate disk to same row count after the snapshot was taken.
            await File.WriteAllTextAsync(replacePath,
                "id,label,amount,when_utc,token\n"
                + "1,mut-a,1.0,2024-01-01,6f9619ff-8b86-d011-b42d-00c04fc964ff\n"
                + "2,mut-b,2.0,2024-01-02,00000000-0000-0000-0000-000000000000\n"
                + "3,mut-c,3.0,2024-01-03,11111111-2222-3333-4444-555555555555\n",
                new UTF8Encoding(false), ct);
            var replacePlan = new CsvImportPlan(replaceRef, CsvImportMode.Replace, appendMappings, null, false);
            var refused = await session.ApplyCsvImportAsync(replacePlan, replaceSource, null, ct);
            RequireEqual(CsvImportOutcome.RefusedInvalidData, refused.Outcome, "fingerprint mismatch must refuse");
            var afterRefuse = await other.QueryRawAsync(
                $"SELECT COUNT(*) FROM {Table(replaceName)};", null, 5, ct);
            RequireEqual(1, Convert.ToInt32(afterRefuse.Rows[0][0], CultureInfo.InvariantCulture),
                "Replace fingerprint refuse must leave the old row");

            // Lossy Append against decimal(10,2) refuses before begin; destination unchanged.
            const string tightName = "t_csv_tight";
            await ExecAsync(session, $"""
                CREATE TABLE {Table(tightName)} (
                  id int NULL,
                  amount decimal(10, 2) NULL
                );
                """, ct);
            _tables.Add(tightName);
            await ExecAsync(session, $"INSERT INTO {Table(tightName)} (id, amount) VALUES (1, CONVERT(decimal(10, 2), '1.00'));", ct);
            var lossyPath = Path.Combine(Path.GetTempPath(), "sqloryn-acc-lossy-" + Guid.NewGuid().ToString("N") + ".csv");
            await File.WriteAllTextAsync(lossyPath, "id,amount\n2,1.234\n", new UTF8Encoding(false), ct);
            try
            {
                var lossyPlan = new CsvImportPlan(
                    new TableRef(_database, _schema, tightName),
                    CsvImportMode.Append,
                    [
                        new CsvColumnMapping(0, "id", CsvSqlType.Int, Destination: new CsvDestinationType("int")),
                        new CsvColumnMapping(1, "amount", CsvSqlType.Decimal, Scale: 2,
                            Destination: new CsvDestinationType("decimal", Precision: 10, Scale: 2))
                    ],
                    null,
                    false);
                var lossy = await session.ApplyCsvImportAsync(
                    lossyPlan, CsvImportSource.LoadImmutable(lossyPath, new CsvImportOptions()), null, ct);
                RequireEqual(CsvImportOutcome.RefusedInvalidData, lossy.Outcome, "lossy decimal must refuse");
                var tightRows = await other.QueryRawAsync(
                    $"SELECT COUNT(*), CONVERT(varchar(20), MAX(amount)) FROM {Table(tightName)};", null, 5, ct);
                RequireEqual(1, Convert.ToInt32(tightRows.Rows[0][0], CultureInfo.InvariantCulture), "lossy refuse row count");
                RequireEqual("1.00", Convert.ToString(tightRows.Rows[0][1], CultureInfo.InvariantCulture) ?? "", "lossy refuse amount");
            }
            finally
            {
                try { File.Delete(lossyPath); } catch { /* synthetic */ }
            }

            Require(session.IsConnected, "Import session dropped after a committed import.");
        }
        finally
        {
            try { File.Delete(path); } catch { /* the temp file holds only synthetic rows */ }
        }
    }

    async Task RequireCsvRowsAsync(SqlSession other, string table, bool includeSentinel, CancellationToken ct)
    {
        var rows = await other.QueryRawAsync($"""
            SELECT id,
                   label,
                   CONVERT(varchar(50), amount) AS amount_text,
                   CONVERT(varchar(30), when_utc, 121) AS when_text,
                   LOWER(CONVERT(varchar(36), token)) AS token_text
            FROM {table}
            ORDER BY id;
            """, null, 20, ct);
        Require(!rows.Truncated && !rows.HasOmittedCells, "CSV verification read was truncated.");
        (int Id, string Label, string Amount, string When, string Token)[] expected =
        [
            (1, "alpha", "10.500000000", "2024-02-29 13:45:30.1234567", "6f9619ff-8b86-d011-b42d-00c04fc964ff"),
            (2, "a,b \"q\"", "-0.000100000", "1999-12-31 00:00:00.0000000", "00000000-0000-0000-0000-000000000000"),
            (3, "beyond-sample", "1234567890.123456789", "2020-01-02 00:00:00.0000000", "11111111-2222-3333-4444-555555555555")
        ];
        if (includeSentinel)
        {
            expected =
            [
                ..expected,
                (9, "sentinel-keep", "7.125000000", "2019-03-04 05:06:07.0000001", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
            ];
        }
        RequireEqual(expected.Length, rows.Rows.Count, "stored CSV row count for " + table);
        for (var i = 0; i < expected.Length; i++)
        {
            var row = rows.Rows[i];
            RequireEqual(expected[i].Id, Convert.ToInt32(row[0], CultureInfo.InvariantCulture), $"csv id {expected[i].Id}");
            RequireEqual(expected[i].Label, Convert.ToString(row[1], CultureInfo.InvariantCulture) ?? "", $"csv label {expected[i].Id}");
            RequireEqual(expected[i].Amount, Convert.ToString(row[2], CultureInfo.InvariantCulture) ?? "", $"csv amount {expected[i].Id}");
            RequireEqual(expected[i].When, Convert.ToString(row[3], CultureInfo.InvariantCulture) ?? "", $"csv datetime {expected[i].Id}");
            RequireEqual(expected[i].Token, Convert.ToString(row[4], CultureInfo.InvariantCulture) ?? "", $"csv guid {expected[i].Id}");
        }
    }

    // ---- 10. security preview -------------------------------------------------------------------------------

    async Task SecurityPreviewAsync(SqlSession session, CancellationToken ct)
    {
        var user = "accu" + Guid.NewGuid().ToString("N")[..12];
        var quoted = SqlIdentity.QuoteIdentifier(user);
        try
        {
            await ExecAsync(session, $"CREATE USER {quoted} WITHOUT LOGIN;", ct);
        }
        catch (AcceptanceFailure error)
        {
            throw new AcceptanceSkip("cannot create a login-less user for the permission preview: " + error.Message);
        }
        _users.Add(user);

        await using var other = await ConnectAsync(ct);
        var before = await PermissionSnapshotAsync(other, user, ct);
        var preview = PermissionChangePreview.Create(
        [
            new PermissionChange(
                PermissionAction.Grant,
                PermissionScope.Schema,
                "SELECT",
                user,
                Database: _database,
                Schema: _schema)
        ]);
        Require(preview.HasChanges, "GRANT preview produced no statement.");
        Require(preview.ExecutableSql.Contains("GRANT", StringComparison.Ordinal), "Preview SQL is not a GRANT.");
        Require(preview.ExecutableSql.Contains(quoted, StringComparison.Ordinal), "Preview SQL does not name the user.");
        // Preview is pure. ApplyPermissionChangeAsync is intentionally not called.
        var after = await PermissionSnapshotAsync(other, user, ct);
        RequireEqual(before, after, "permissions after GRANT preview");
        Require(!after.Contains("SELECT", StringComparison.Ordinal), "A SELECT permission appeared without applying the preview.");
    }

    async Task<string> PermissionSnapshotAsync(SqlSession session, string user, CancellationToken ct)
    {
        var rows = await session.QueryRawAsync("""
            SELECT p.state_desc, p.permission_name, p.class_desc, COALESCE(SCHEMA_NAME(p.major_id), N'') AS schema_name
            FROM sys.database_permissions AS p
            WHERE p.grantee_principal_id = DATABASE_PRINCIPAL_ID(@u)
            ORDER BY p.class_desc, p.permission_name, p.state_desc, schema_name;
            """, [new EditParameter("@u", user)], 100, ct);
        Require(!rows.Truncated, "Permission snapshot was truncated.");
        var sb = new StringBuilder();
        foreach (var row in rows.Rows)
        {
            foreach (var value in row)
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\u001f');
            sb.Append('\u001e');
        }
        return sb.ToString();
    }

    // ---- 11. backup -----------------------------------------------------------------------------------------

    async Task BackupAsync(SqlSession session, CancellationToken ct)
    {
        var directory = await ScalarAsync(session, "SELECT CONVERT(nvarchar(260), SERVERPROPERTY('InstanceDefaultDataPath'));", ct) as string;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = await ScalarAsync(session, """
                SELECT LEFT(physical_name, LEN(physical_name) - CHARINDEX('\', REVERSE(REPLACE(physical_name, '/', '\'))))
                FROM sys.master_files WHERE database_id = 1 AND file_id = 1;
                """, ct) as string;
        }
        if (string.IsNullOrWhiteSpace(directory))
            throw new AcceptanceSkip("SQL Server did not report a data directory, so there is no known server-side backup path.");

        var name = "accbak" + Guid.NewGuid().ToString("N")[..12];
        var quoted = SqlIdentity.QuoteIdentifier(name);
        var created = false;
        try
        {
            try
            {
                await ExecAsync(session, $"CREATE DATABASE {quoted};", ct);
                created = true;
                _databases.Add(name);
            }
            catch (AcceptanceFailure error) when (!created)
            {
                throw new AcceptanceSkip("cannot create a disposable database to back up: " + error.Message);
            }

            var slash = Math.Max(directory.LastIndexOf('/'), directory.LastIndexOf('\\'));
            var separator = slash >= 0 && directory[slash] == '\\' ? '\\' : '/';
            var devicePath = directory.TrimEnd('/', '\\') + separator + name + ".bak";
            var plan = new BackupRestorePlan
            {
                Operation = OperationKind.Backup,
                Database = name,
                Device = new BackupDevice(devicePath, BackupPathKind.ServerSide),
                CopyOnly = true,
                Checksum = true,
                Overwrite = false,
                ServerRunsOnThisMac = false
            };
            var issues = plan.Validate().Where(i => i.Severity == OperationIssueSeverity.Error).ToList();
            if (issues.Count > 0)
                throw new AcceptanceSkip("backup plan is not usable on this server: " + issues[0].Message);

            await using var other = await ConnectAsync(ct);
            var before = (int)(await ScalarAsync(other, """
                SELECT COUNT(*) FROM msdb.dbo.backupset
                WHERE database_name = @n AND type = 'D' AND is_copy_only = 1;
                """, ct, new EditParameter("@n", name)))!;
            RequireEqual(0, before, "backupset rows before execute");
            var preview = plan.GeneratePreviewSql();
            Require(preview.Contains("BACKUP DATABASE", StringComparison.Ordinal), "Preview is not a BACKUP DATABASE statement.");
            var unchanged = (int)(await ScalarAsync(other, """
                SELECT COUNT(*) FROM msdb.dbo.backupset
                WHERE database_name = @n AND type = 'D' AND is_copy_only = 1;
                """, ct, new EditParameter("@n", name)))!;
            RequireEqual(0, unchanged, "generating the backup preview wrote a backupset row");

            var result = await OperationRunner.ExecuteAsync(
                plan, preview, confirmed: true, (sql, token) => session.ExecuteAsync(sql, token), ct);
            if (result.Outcome != OperationOutcome.Completed)
                throw new AcceptanceSkip("server did not complete the backup (" + result.Outcome + "): " + result.Message);

            var after = (int)(await ScalarAsync(other, """
                SELECT COUNT(*) FROM msdb.dbo.backupset
                WHERE database_name = @n AND type = 'D' AND is_copy_only = 1;
                """, ct, new EditParameter("@n", name)))!;
            Require(after >= 1, "The backup completed but msdb.dbo.backupset has no copy-only row for the disposable database.");
        }
        finally
        {
            if (created)
            {
                try
                {
                    using var drop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await ExecAsync(session, $"DROP DATABASE IF EXISTS {quoted};", drop.Token);
                }
                catch (Exception error)
                {
                    Report("CORE-CLEANUP", "FAIL", _redactor.Clean("drop disposable backup database: " + error.Message));
                }
            }
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
        foreach (var database in _databases)
            await TryAsync(admin, $"DROP DATABASE IF EXISTS {SqlIdentity.QuoteIdentifier(database)};");
        if (!schemaCreated)
            return;
        foreach (var user in _users)
            await TryAsync(admin, $"DROP USER IF EXISTS {SqlIdentity.QuoteIdentifier(user)};");
        foreach (var table in _tables.AsEnumerable().Reverse())
            await TryAsync(admin, $"DROP TABLE IF EXISTS {Table(table)};");
        await TryAsync(admin, $"DROP SCHEMA IF EXISTS {SqlIdentity.QuoteIdentifier(_schema)};");
    }
}
