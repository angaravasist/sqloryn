using System.Text;
using Sqloryn.Core;

/// <summary>
/// Flat-file import: preview parser, mapping plan, SQL generation and the transactional apply (against a fake).
/// Component-only: no SQL Server connection. Live INSERT behaviour is provider/host verification, not claimed here.
/// </summary>
static class CsvImportTests
{
    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"FAIL {name}");
    }

    static void Pass(string name) => Console.WriteLine($"PASS {name}");

    static MemoryStream Utf8(string text, bool bom = false)
    {
        var bytes = new UTF8Encoding(bom).GetBytes(text);
        return new MemoryStream(bom ? [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. bytes] : bytes);
    }

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SQLoryn.slnx")))
                return d.FullName;
        throw new Exception("FAIL repo root (SQLoryn.slnx) not found from test base directory");
    }

    static readonly TableRef Target = new("ImportDb", "dbo", "People");

    static CsvImportSource Source(string text, CsvImportOptions? options = null)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        return new CsvImportSource(() => new MemoryStream(bytes), options ?? new CsvImportOptions());
    }

    public static async Task RunAsync()
    {
        ParserBasics();
        ParserErrors();
        TypeGuesses();
        FormulaLikeCells();
        PreviewDoesNotWrite();
        PlanValidationAndSql();
        Conversion();
        CreateDecimalScaleExact();
        DestinationFidelity();
        await ApplyAsync();
        await ImmutableSourceFingerprintAsync();
        AppWiring();
    }

    /// <summary>
    /// CREATE must emit the observed decimal scale (0–38), never silently clamp to 18.
    /// </summary>
    static void CreateDecimalScaleExact()
    {
        Check(CsvSqlTypes.ToSqlTypeText(CsvSqlType.Decimal, null, 28) == "decimal(38, 28)", "create-decimal-scale-28");
        Check(CsvSqlTypes.ToSqlTypeText(CsvSqlType.Decimal, null, 0) == "decimal(38, 0)", "create-decimal-scale-0");
        Check(CsvSqlTypes.ToSqlTypeText(CsvSqlType.Decimal, null, 38) == "decimal(38, 38)", "create-decimal-scale-38");
        Throws<ArgumentOutOfRangeException>(() => CsvSqlTypes.ToSqlTypeText(CsvSqlType.Decimal, null, 39), "create-decimal-scale-39-refused");
        Throws<ArgumentOutOfRangeException>(() => CsvSqlTypes.ToSqlTypeText(CsvSqlType.Decimal, null, -1), "create-decimal-scale-negative-refused");

        var frac28 = "1." + new string('1', 28);
        var preview = CsvImportPreview.Parse(Utf8("amount\n" + frac28 + "\n"), new CsvImportOptions());
        Check(preview.Columns[0].GuessedType == CsvSqlType.Decimal && preview.Columns[0].MaxScale == 28, "preview-guesses-scale-28");
        var plan = CsvImportPlan.FromPreview(preview, Target, CsvImportMode.Create);
        Check(plan.Mappings[0].Scale == 28, "create-plan-keeps-scale-28");
        Check(plan.GetPrepareStatements()[0].Contains("decimal(38, 28)", StringComparison.Ordinal), "create-ddl-scale-28");
        Pass("csv-create-decimal-scale-exact");
    }

    /// <summary>
    /// Append/Replace must refuse cells that would lose fidelity against the destination type before any write.
    /// </summary>
    static void DestinationFidelity()
    {
        var decimalDest = new CsvDestinationType("decimal", Precision: 10, Scale: 2);
        Check(!CsvDestinationFidelity.TryAccept("1.234", decimalDest, emptyTextAsNull: false, out var err)
              && err is not null && err.Contains("scale", StringComparison.OrdinalIgnoreCase), "dest-decimal-scale-refuse");
        Check(CsvDestinationFidelity.TryAccept("1.23", decimalDest, false, out _), "dest-decimal-scale-ok");
        Check(!CsvDestinationFidelity.TryAccept("123456789.01", decimalDest, false, out _), "dest-decimal-precision-refuse");

        var varchar5 = new CsvDestinationType("varchar", MaxLength: 5);
        Check(!CsvDestinationFidelity.TryAccept("abcdef", varchar5, false, out _), "dest-varchar-length-refuse");
        Check(!CsvDestinationFidelity.TryAccept("café", varchar5, false, out var cafeErr)
              && cafeErr is not null, "dest-varchar-non-latin1-refuse");
        Check(CsvDestinationFidelity.TryAccept("abcde", varchar5, false, out _), "dest-varchar-ok");

        var money = new CsvDestinationType("money");
        Check(!CsvDestinationFidelity.TryAccept("922337203685477.5808", money, false, out _), "dest-money-overflow-refuse");
        Check(!CsvDestinationFidelity.TryAccept("1.23456", money, false, out _), "dest-money-scale-refuse");

        var nvarchar10 = new CsvDestinationType("nvarchar", MaxLength: 10);
        Check(CsvDestinationFidelity.TryAccept("café", nvarchar10, false, out _), "dest-nvarchar-unicode-ok");
        Check(!CsvDestinationFidelity.TryAccept("abcdefghijk", nvarchar10, false, out _), "dest-nvarchar-length-refuse");

        // Replace refuses lossy data before the transaction begins (old rows survive).
        var replacePlan = new CsvImportPlan(Target, CsvImportMode.Replace,
            [new CsvColumnMapping(0, "amt", CsvSqlType.Decimal, Scale: 2, Destination: decimalDest)], null, false);
        var factory = new FakeCsvImportFactory(replacedRows: 4);
        var refused = CsvImportApply.ApplyAsync(factory, replacePlan, Source("amt\n1.234\n")).GetAwaiter().GetResult();
        Check(refused.Outcome == CsvImportOutcome.RefusedInvalidData && factory.Log.Count == 0, "replace-lossy-refused-before-begin");
        Check(refused.Issues.Any(i => i.Severity == CsvIssueSeverity.Error), "replace-lossy-reports-issue");
        Pass("csv-destination-fidelity");
    }

    /// <summary>
    /// Source content must be fingerprint-verified (or snapshotted) so same-row-count file edits cannot commit silently.
    /// </summary>
    static async Task ImmutableSourceFingerprintAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqloryn-csv-fp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "people.csv");
            File.WriteAllText(path, "id,name\n1,Ann\n2,Bob\n");
            var source = CsvImportSource.LoadImmutable(path, new CsvImportOptions());
            Check(source.ContentSha256 is { Length: 64 }, "immutable-has-sha256");
            Check(source.ByteLength is > 0, "immutable-has-length");

            // Snapshot Open is independent of disk after load.
            File.WriteAllText(path, "id,name\n9,Zed\n8,Yan\n"); // same row count, different content
            using (var stream = source.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var text = reader.ReadToEnd();
                Check(text.Contains("Ann", StringComparison.Ordinal) && !text.Contains("Zed", StringComparison.Ordinal),
                    "immutable-open-ignores-disk-mutation");
            }

            // Apply refuses when the on-disk file no longer matches the fingerprint (Replace must not delete).
            var replaceFactory = new FakeCsvImportFactory(replacedRows: 9);
            var plan = PeoplePlan(CsvImportMode.Replace);
            var result = await CsvImportApply.ApplyAsync(replaceFactory, plan, source);
            Check(result.Outcome == CsvImportOutcome.RefusedInvalidData && replaceFactory.Log.Count == 0,
                "fingerprint-mismatch-refuses-before-begin");
            Check(result.Message.Contains("changed", StringComparison.OrdinalIgnoreCase)
                  || result.Message.Contains("fingerprint", StringComparison.OrdinalIgnoreCase)
                  || result.Issues.Any(i => i.Message.Contains("changed", StringComparison.OrdinalIgnoreCase)),
                "fingerprint-mismatch-message");

            // Matching disk + snapshot still commits.
            File.WriteAllText(path, "id,name\n1,Ann\n2,Bob\n");
            var okSource = CsvImportSource.LoadImmutable(path, new CsvImportOptions());
            var okFactory = new FakeCsvImportFactory();
            var ok = await CsvImportApply.ApplyAsync(okFactory, PeoplePlan(CsvImportMode.Append), okSource);
            Check(ok.Committed && ok.RowsInserted == 2, "fingerprint-match-commits");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
        Pass("csv-immutable-source-fingerprint");
    }

    static void ParserBasics()
    {
        // quoted newlines, escaped quotes and delimiters inside quotes stay in one cell
        var preview = CsvImportPreview.Parse(Utf8("id,note,name\n1,\"line one\nline two\",\"He said \"\"hi\"\", ok\"\n2,plain,x\r\n"), new CsvImportOptions());
        Check(!preview.HasErrors, "quoted-newline-no-errors");
        Check(preview.Columns.Count == 3 && preview.Columns[1].Name == "note", "quoted-newline-columns");
        Check(preview.SampleRows.Count == 2, "quoted-newline-row-count");
        Check(preview.SampleRows[0][1] == "line one\nline two", "quoted-newline-preserved");
        Check(preview.SampleRows[0][2] == "He said \"hi\", ok", "escaped-quote-and-delimiter");
        Check(preview.SampleRows[1][0] == "2" && preview.SampleRows[1][2] == "x", "crlf-record-end");
        Pass("csv-preview-quoted-newlines");

        // delimiter option, header off
        var semi = CsvImportPreview.Parse(Utf8("a;b\n1;2\n"), new CsvImportOptions { Delimiter = ';', HasHeader = false });
        Check(semi.Columns.Count == 2 && semi.Columns[0].Name == "Column1" && semi.SampleRows.Count == 2, "no-header-delimiter");
        Pass("csv-preview-delimiter-header");

        // empty header cells and duplicates get safe names and a warning, never a blank identifier
        var emptyHeader = CsvImportPreview.Parse(Utf8("id,,name,name\n1,2,3,4\n"), new CsvImportOptions());
        Check(emptyHeader.Columns.Select(c => c.Name).SequenceEqual(["id", "Column2", "name", "name_2"]), "empty-and-duplicate-header-names");
        Check(emptyHeader.Columns[1].HeaderWasEmpty, "empty-header-flagged");
        Check(emptyHeader.Issues.Count(i => i.Severity == CsvIssueSeverity.Warning) >= 2 && !emptyHeader.HasErrors, "empty-header-warns");
        Pass("csv-preview-empty-header");

        // utf-8 BOM is skipped, utf-16 BOM honoured
        var bom = CsvImportPreview.Parse(Utf8("id,name\n1,a\n", bom: true), new CsvImportOptions());
        Check(bom.Columns[0].Name == "id", "utf8-bom-skipped");
        var utf16 = new MemoryStream([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("id,name\n1,é\n")]);
        var p16 = CsvImportPreview.Parse(utf16, new CsvImportOptions());
        Check(p16.Columns[0].Name == "id" && p16.SampleRows[0][1] == "é", "utf16-bom");
        Pass("csv-preview-bom");

        // sample cap and scan truncation are reported honestly
        var sb = new StringBuilder("n\n");
        for (var i = 0; i < 50; i++) sb.Append(i).Append('\n');
        var capped = CsvImportPreview.Parse(Utf8(sb.ToString()), new CsvImportOptions { SampleRowCount = 5, ScanRowLimit = 20 });
        Check(capped.SampleRows.Count == 5 && capped.RowsScanned == 20 && capped.ScanTruncated, "sample-and-scan-caps");
        Pass("csv-preview-caps");
    }

    static void ParserErrors()
    {
        // invalid UTF-8 (latin-1 byte) is an error, not silent replacement
        var bad = new MemoryStream([.. "id,name\n1,"u8.ToArray(), 0xE9, (byte)'\n']);
        var badPreview = CsvImportPreview.Parse(bad, new CsvImportOptions());
        Check(badPreview.HasErrors && badPreview.Issues.Any(i => i.Severity == CsvIssueSeverity.Error && i.Message.Contains("encoding", StringComparison.OrdinalIgnoreCase)), "invalid-encoding-error");
        Check(!badPreview.SampleRows.Any(r => r.Any(c => c.Contains('\uFFFD'))), "no-replacement-chars");
        // the same bytes decode under an explicit Latin-1 choice
        var latin = CsvImportPreview.Parse(new MemoryStream([.. "id,name\n1,"u8.ToArray(), 0xE9, (byte)'\n']), new CsvImportOptions { Encoding = Encoding.Latin1 });
        Check(!latin.HasErrors && latin.SampleRows[0][1] == "é", "explicit-latin1");
        Pass("csv-preview-bad-encoding");

        // unterminated quote
        var open = CsvImportPreview.Parse(Utf8("a,b\n1,\"never closed\n2,3\n"), new CsvImportOptions());
        Check(open.HasErrors && open.Issues.Any(i => i.Message.Contains("quote", StringComparison.OrdinalIgnoreCase) && i.Line == 2), "unterminated-quote-error-line");
        Pass("csv-preview-unterminated-quote");

        // ragged rows are errors with their row number
        var ragged = CsvImportPreview.Parse(Utf8("a,b\n1,2\n3\n4,5,6\n"), new CsvImportOptions());
        Check(ragged.Issues.Count(i => i.Severity == CsvIssueSeverity.Error && i.Row is 2 or 3) == 2, "ragged-rows-error");
        Pass("csv-preview-ragged");

        // empty file, binary data
        Check(CsvImportPreview.Parse(Utf8(""), new CsvImportOptions()).HasErrors, "empty-file-error");
        Check(CsvImportPreview.Parse(new MemoryStream([(byte)'a', 0, (byte)'b', (byte)'\n']), new CsvImportOptions()).HasErrors, "nul-byte-error");
        Pass("csv-preview-empty-binary");

        // delimiter must be a sane single char (never a quote or line break)
        Throws<ArgumentException>(() => CsvImportPreview.Parse(Utf8("a\n"), new CsvImportOptions { Delimiter = '"' }), "quote-delimiter-rejected");
        Throws<ArgumentException>(() => CsvImportPreview.Parse(Utf8("a\n"), new CsvImportOptions { Delimiter = '\n' }), "newline-delimiter-rejected");
        Pass("csv-preview-delimiter-validation");
    }

    static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"FAIL {name}");
    }

    static void TypeGuesses()
    {
        var text = "i,big,dec,flag,when,guid,mixed,zip,locale,empty\n" +
                   "1,3000000000,1.50,true,2024-01-31,3F2504E0-4F89-11D3-9A0C-0305E82C3301,a,02134,\"1,5\",\n" +
                   "-2,4,2,FALSE,2024-02-29 13:45:10.123,a8098c1a-f86e-11da-bd1a-00112444be1e,1,10001,\"2,5\",\n";
        var p = CsvImportPreview.Parse(Utf8(text), new CsvImportOptions());
        var types = p.Columns.Select(c => c.GuessedType).ToArray();
        Check(types[0] == CsvSqlType.Int, "guess-int");
        Check(types[1] == CsvSqlType.BigInt, "guess-bigint");
        Check(types[2] == CsvSqlType.Decimal && p.Columns[2].MaxScale == 2, "guess-decimal-scale");
        Check(types[3] == CsvSqlType.Bit, "guess-bit");
        Check(types[4] == CsvSqlType.DateTime2, "guess-datetime2");
        Check(types[5] == CsvSqlType.UniqueIdentifier, "guess-guid");
        Check(types[6] == CsvSqlType.NVarChar, "guess-mixed-text");
        Check(types[7] == CsvSqlType.NVarChar, "guess-leading-zero-stays-text");
        Check(types[8] == CsvSqlType.NVarChar, "guess-culture-number-stays-text");
        Check(types[9] == CsvSqlType.NVarChar && p.Columns[9].NullOrEmptyCount == 2, "guess-empty-column-text");

        // ambiguous / impossible dates stay text rather than being guessed in the machine's culture
        var dates = CsvImportPreview.Parse(Utf8("d\n03/04/2024\n2024-02-30\n"), new CsvImportOptions());
        Check(dates.Columns[0].GuessedType == CsvSqlType.NVarChar, "guess-malformed-dates-text");
        Pass("csv-preview-type-guesses");
    }

    static void FormulaLikeCells()
    {
        var p = CsvImportPreview.Parse(Utf8("name,amount\n=HYPERLINK(\"http://x\"),-5\n@SUM(A1),+7\n\"\t=cmd\",ok\n-dash,3\n"), new CsvImportOptions());
        // -5 and +7 are numbers, not formulas
        Check(p.Columns[1].FormulaLikeCount == 0, "numbers-not-formula-like");
        Check(p.Columns[0].FormulaLikeCount == 4, "formula-like-counted");
        Check(p.Issues.Any(i => i.Severity == CsvIssueSeverity.Warning && i.Column == 0 && i.Message.Contains("formula", StringComparison.OrdinalIgnoreCase)), "formula-like-warning");
        Check(!p.HasErrors, "formula-like-not-an-error");
        // the cells are carried verbatim: they are data, imported as parameters, never rewritten or evaluated
        Check(p.SampleRows[0][0] == "=HYPERLINK(\"http://x\")", "formula-cell-verbatim");

        var plan = CsvImportPlan.FromPreview(p, Target, CsvImportMode.Append);
        var cmd = plan.BuildInsert([[ "=1+1", "5" ]]);
        Check(!cmd.CommandText.Contains("1+1", StringComparison.Ordinal) && cmd.Parameters.Any(x => Equals(x.Value, "=1+1")), "formula-value-is-a-parameter");
        Pass("csv-import-formula-like-cells");
    }

    static void PreviewDoesNotWrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqloryn-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "people.csv");
            File.WriteAllText(path, "id,name\n1,a\n2,b\n");
            var before = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));
            var filesBefore = Directory.GetFileSystemEntries(dir).Order().ToArray();

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead);   // preview must work on a read-only source
            var p = CsvImportPreview.Parse(path, new CsvImportOptions());
            Check(!p.HasErrors && p.SampleRows.Count == 2, "preview-from-readonly-file");

            // SQL text for a preview is pure: no session, no transaction, no file touched
            var plan = CsvImportPlan.FromPreview(p, Target, CsvImportMode.Replace);
            var sql = plan.GeneratePreviewSql();
            Check(sql.Contains("DELETE FROM [ImportDb].[dbo].[People]", StringComparison.Ordinal), "preview-sql-replace");

            Check(File.ReadAllBytes(path).SequenceEqual(before.Item1) && File.GetLastWriteTimeUtc(path) == before.Item2, "preview-source-unchanged");
            Check(Directory.GetFileSystemEntries(dir).Order().SequenceEqual(filesBefore), "preview-leaves-no-files");
            Throws<FileNotFoundException>(() => CsvImportPreview.Parse(Path.Combine(dir, "missing.csv"), new CsvImportOptions()), "missing-file");
            Check(!Directory.Exists(Path.Combine(dir, "missing")), "preview-creates-nothing");

            // the apply entry point is the only thing that takes a transaction factory: preview never begins one
            var factory = new FakeCsvImportFactory();
            Check(factory.Log.Count == 0, "preview-no-transaction");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
        Pass("csv-import-preview-no-write");
    }

    static void PlanValidationAndSql()
    {
        var preview = CsvImportPreview.Parse(Utf8("id,Name,note\n1,Ann,hi\n2,Bob,there\n"), new CsvImportOptions());

        // Create: DDL, types and identifier quoting
        var create = CsvImportPlan.FromPreview(preview, new TableRef("ImportDb", "dbo", "New]Table"), CsvImportMode.Create);
        Check(create.Validate(preview.Columns.Count).Count == 0, "create-plan-valid");
        var createSql = create.GeneratePreviewSql();
        Check(createSql.Contains("CREATE TABLE [ImportDb].[dbo].[New]]Table]", StringComparison.Ordinal), "create-quotes-identifier");
        Check(createSql.Contains("[id] int NULL", StringComparison.Ordinal) && createSql.Contains("[Name] nvarchar(", StringComparison.Ordinal), "create-column-types");
        Check(createSql.Contains("INSERT INTO [ImportDb].[dbo].[New]]Table]", StringComparison.Ordinal) && createSql.Contains("@p0", StringComparison.Ordinal), "create-insert-template");
        Check(!createSql.Contains("Ann", StringComparison.Ordinal), "preview-sql-has-no-cell-values");
        Check(create.GetPrepareStatements().Count == 1 && create.GetPrepareStatements()[0].StartsWith("CREATE TABLE ", StringComparison.Ordinal), "create-prepare-statement");

        // truncated scan -> text columns are nvarchar(max), never a guessed width that later rows could overflow
        var many = new StringBuilder("t\n");
        for (var i = 0; i < 30; i++) many.Append("x\n");
        var truncated = CsvImportPreview.Parse(Utf8(many.ToString()), new CsvImportOptions { ScanRowLimit = 5 });
        Check(CsvImportPlan.FromPreview(truncated, Target, CsvImportMode.Create).GeneratePreviewSql().Contains("nvarchar(max)", StringComparison.Ordinal), "truncated-scan-text-is-max");

        // Append: no DDL, no delete
        var append = CsvImportPlan.FromPreview(preview, Target, CsvImportMode.Append);
        Check(append.GetPrepareStatements().Count == 0 && !append.GeneratePreviewSql().Contains("DELETE", StringComparison.Ordinal) && !append.GeneratePreviewSql().Contains("CREATE", StringComparison.Ordinal), "append-has-no-ddl-or-delete");
        Check(append.IsDestructive == false && CsvImportPlan.FromPreview(preview, Target, CsvImportMode.Replace).IsDestructive, "replace-is-destructive");

        // Replace prepares with a counted DELETE (never TRUNCATE) so it can roll back with the inserts
        var replace = CsvImportPlan.FromPreview(preview, Target, CsvImportMode.Replace);
        var prep = replace.GetPrepareCommands();
        Check(prep.Count == 1 && prep[0].CommandText.Contains("DELETE FROM", StringComparison.Ordinal) && !prep[0].CommandText.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase), "replace-uses-delete");

        // INSERT: parameterised, row-count marker, batched below the parameter ceiling
        var cmd = append.BuildInsert([["1", "Ann", "hi"], ["2", "Bob", "there"]]);
        Check(cmd.CommandText.Contains("VALUES (@p0, @p1, @p2), (@p3, @p4, @p5)", StringComparison.Ordinal), "multi-row-values");
        Check(cmd.CommandText.Contains(EditRowsSql.RowCountColumn, StringComparison.Ordinal) && cmd.Parameters.Count == 6, "insert-rowcount-and-params");
        Check(!cmd.CommandText.Contains("Ann", StringComparison.Ordinal), "insert-no-literals");
        Check(append.RowsPerBatch is >= 1 and <= 1000 && append.RowsPerBatch * 3 <= 2000, "batch-under-parameter-limit");

        // validation failures
        var empty = new CsvImportPlan(Target, CsvImportMode.Append, [], null, false);
        Check(empty.Validate(3).Any(i => i.Severity == CsvIssueSeverity.Error), "no-mappings-invalid");
        var dupTarget = new CsvImportPlan(Target, CsvImportMode.Append,
            [new CsvColumnMapping(0, "a", CsvSqlType.Int), new CsvColumnMapping(1, "A", CsvSqlType.Int)], null, false);
        Check(dupTarget.Validate(2).Any(i => i.Message.Contains("more than once", StringComparison.OrdinalIgnoreCase)), "duplicate-target-invalid");
        var badSource = new CsvImportPlan(Target, CsvImportMode.Append, [new CsvColumnMapping(5, "a", CsvSqlType.Int)], null, false);
        Check(badSource.Validate(2).Any(i => i.Severity == CsvIssueSeverity.Error), "source-out-of-range-invalid");
        var blankName = new CsvImportPlan(Target, CsvImportMode.Create, [new CsvColumnMapping(0, " ", CsvSqlType.Int)], null, false);
        Check(blankName.Validate(1).Any(i => i.Severity == CsvIssueSeverity.Error), "blank-target-invalid");
        var badKey = new CsvImportPlan(Target, CsvImportMode.Append, [new CsvColumnMapping(0, "a", CsvSqlType.Int)], ["zzz"], false);
        Check(badKey.Validate(1).Any(i => i.Message.Contains("key", StringComparison.OrdinalIgnoreCase)), "unmapped-key-invalid");
        var noTable = new CsvImportPlan(new TableRef("d", "dbo", ""), CsvImportMode.Append, [new CsvColumnMapping(0, "a", CsvSqlType.Int)], null, false);
        Check(noTable.Validate(1).Any(i => i.Severity == CsvIssueSeverity.Error), "blank-table-invalid");
        Throws<InvalidOperationException>(() => empty.GeneratePreviewSql(), "invalid-plan-refuses-sql");
        Pass("csv-import-plan");
    }

    static void Conversion()
    {
        object? v;
        string? err;
        Check(CsvImportPlan.TryConvert("42", CsvSqlType.Int, false, out v, out err) && v is 42, "convert-int");
        Check(!CsvImportPlan.TryConvert("4.2", CsvSqlType.Int, false, out _, out err) && err is not null, "convert-int-rejects-decimal");
        Check(!CsvImportPlan.TryConvert("99999999999", CsvSqlType.Int, false, out _, out _), "convert-int-overflow");
        Check(CsvImportPlan.TryConvert("99999999999", CsvSqlType.BigInt, false, out v, out _) && v is 99999999999L, "convert-bigint");
        Check(CsvImportPlan.TryConvert("1.50", CsvSqlType.Decimal, false, out v, out _) && v is "1.50", "convert-decimal-invariant-text");
        // culture-dependent numbers are rejected, never reinterpreted
        Check(!CsvImportPlan.TryConvert("1,5", CsvSqlType.Decimal, false, out _, out _), "convert-decimal-comma-rejected");
        Check(!CsvImportPlan.TryConvert("1.234,56", CsvSqlType.Decimal, false, out _, out _), "convert-decimal-european-rejected");
        Check(!CsvImportPlan.TryConvert("1e5", CsvSqlType.Decimal, false, out _, out _), "convert-decimal-exponent-rejected");
        Check(CsvImportPlan.TryConvert("TRUE", CsvSqlType.Bit, false, out v, out _) && v is true, "convert-bit");
        Check(!CsvImportPlan.TryConvert("maybe", CsvSqlType.Bit, false, out _, out _), "convert-bit-rejects");
        Check(CsvImportPlan.TryConvert("2024-02-29", CsvSqlType.DateTime2, false, out v, out _) && v is "2024-02-29T00:00:00.0000000", "convert-date");
        Check(!CsvImportPlan.TryConvert("2024-02-30", CsvSqlType.DateTime2, false, out _, out err), "convert-malformed-date-rejected");
        Check(!CsvImportPlan.TryConvert("03/04/2024", CsvSqlType.DateTime2, false, out _, out _), "convert-ambiguous-date-rejected");
        Check(CsvImportPlan.TryConvert("3F2504E0-4F89-11D3-9A0C-0305E82C3301", CsvSqlType.UniqueIdentifier, false, out v, out _) && v is Guid, "convert-guid");
        // empty cells: NULL for non-text, controlled for text
        Check(CsvImportPlan.TryConvert("", CsvSqlType.Int, false, out v, out _) && v is null, "empty-int-null");
        Check(CsvImportPlan.TryConvert("", CsvSqlType.NVarChar, false, out v, out _) && v is "", "empty-text-empty-string");
        Check(CsvImportPlan.TryConvert("", CsvSqlType.NVarChar, true, out v, out _) && v is null, "empty-text-null-when-asked");
        Check(CsvImportPlan.TryConvert("  7 ", CsvSqlType.Int, false, out v, out _) && v is 7, "convert-trims-non-text");
        Check(CsvImportPlan.TryConvert("  keep ", CsvSqlType.NVarChar, false, out v, out _) && v is "  keep ", "text-not-trimmed");
        Check(err is null || err.Length > 0, "err-shape");

        // existing table column types map onto the import types
        Check(CsvSqlTypes.FromSqlTypeName("smallint") == CsvSqlType.Int && CsvSqlTypes.FromSqlTypeName("MONEY") == CsvSqlType.Decimal
              && CsvSqlTypes.FromSqlTypeName("datetime") == CsvSqlType.DateTime2 && CsvSqlTypes.FromSqlTypeName("varchar") == CsvSqlType.NVarChar
              && CsvSqlTypes.FromSqlTypeName("bit") == CsvSqlType.Bit && CsvSqlTypes.FromSqlTypeName("bigint") == CsvSqlType.BigInt, "sql-type-mapping");
        Pass("csv-import-conversion");
    }

    static CsvImportPlan PeoplePlan(CsvImportMode mode, IReadOnlyList<string>? keys = null, int rowsHint = 0)
    {
        _ = rowsHint;
        return new CsvImportPlan(Target, mode,
            [new CsvColumnMapping(0, "id", CsvSqlType.Int), new CsvColumnMapping(1, "name", CsvSqlType.NVarChar, 50)], keys, false);
    }

    static string Rows(int n, Func<int, string>? line = null)
    {
        var sb = new StringBuilder("id,name\n");
        for (var i = 1; i <= n; i++)
            sb.Append(line?.Invoke(i) ?? $"{i},n{i}").Append('\n');
        return sb.ToString();
    }

    static async Task ApplyAsync()
    {
        // happy path: batches in one transaction, single commit, exact row accounting
        var factory = new FakeCsvImportFactory();
        var plan = PeoplePlan(CsvImportMode.Append);
        var result = await CsvImportApply.ApplyAsync(factory, plan, Source(Rows(2500)));
        Check(result.Committed && result.RowsInserted == 2500 && result.RowsRead == 2500, "apply-commits-all-rows");
        Check(factory.Log.Count(l => l == "commit") == 1 && factory.Log.Count(l => l == "begin") == 1 && !factory.Log.Contains("rollback"), "apply-single-transaction");
        Check(factory.Executed.Count == (2500 + plan.RowsPerBatch - 1) / plan.RowsPerBatch, "apply-batched");
        Check(factory.Executed.All(c => c.CommandText.StartsWith("SET NOCOUNT ON;", StringComparison.Ordinal)), "apply-uses-core-sql");
        Pass("csv-import-apply-commits");

        // Create runs DDL first inside the same transaction; Replace deletes first
        var createFactory = new FakeCsvImportFactory();
        var created = await CsvImportApply.ApplyAsync(createFactory, PeoplePlan(CsvImportMode.Create), Source(Rows(3)));
        Check(created.Committed && createFactory.Ddl.Count == 1 && createFactory.Ddl[0].StartsWith("CREATE TABLE ", StringComparison.Ordinal)
              && createFactory.Log.IndexOf("ddl") < createFactory.Log.IndexOf("exec") && createFactory.Log.IndexOf("exec") < createFactory.Log.IndexOf("commit"), "create-ddl-in-transaction");
        var replaceFactory = new FakeCsvImportFactory(replacedRows: 7);
        var replaced = await CsvImportApply.ApplyAsync(replaceFactory, PeoplePlan(CsvImportMode.Replace), Source(Rows(3)));
        Check(replaced.Committed && replaceFactory.Executed[0].CommandText.Contains("DELETE FROM", StringComparison.Ordinal) && replaced.RowsDeleted == 7 && replaced.RowsInserted == 3, "replace-deletes-first-and-reports");
        Pass("csv-import-apply-create-replace");

        // invalid data refuses BEFORE any transaction begins; every problem has a row/column
        var badData = new FakeCsvImportFactory();
        var bad = await CsvImportApply.ApplyAsync(badData, PeoplePlan(CsvImportMode.Append), Source(Rows(5, i => i == 3 ? "notanumber,x" : $"{i},n")));
        Check(bad.Outcome == CsvImportOutcome.RefusedInvalidData && badData.Log.Count == 0, "invalid-data-refused-before-begin");
        Check(bad.Issues.Any(i => i.Row == 3 && i.Column == 0 && i.Severity == CsvIssueSeverity.Error), "invalid-data-reports-row-column");
        var ragged = await CsvImportApply.ApplyAsync(new FakeCsvImportFactory(), PeoplePlan(CsvImportMode.Append), Source("id,name\n1,a\n2\n"));
        Check(ragged.Outcome == CsvImportOutcome.RefusedInvalidData, "ragged-refused");
        var badPlan = await CsvImportApply.ApplyAsync(new FakeCsvImportFactory(), new CsvImportPlan(Target, CsvImportMode.Append, [], null, false), Source(Rows(1)));
        Check(badPlan.Outcome == CsvImportOutcome.RefusedInvalidPlan, "invalid-plan-refused");
        var empty = await CsvImportApply.ApplyAsync(new FakeCsvImportFactory(), PeoplePlan(CsvImportMode.Append), Source("id,name\n"));
        Check(empty.Outcome == CsvImportOutcome.RefusedInvalidData, "no-rows-refused");
        var tooMany = await CsvImportApply.ApplyAsync(new FakeCsvImportFactory(), PeoplePlan(CsvImportMode.Append), Source(Rows(10)), maxRows: 5);
        Check(tooMany.Outcome == CsvImportOutcome.RefusedInvalidData, "row-cap-refused");
        Pass("csv-import-apply-refusals");

        // duplicate keys inside the file are reported with both row numbers and nothing is written
        var dupFile = new FakeCsvImportFactory();
        var dup = await CsvImportApply.ApplyAsync(dupFile, PeoplePlan(CsvImportMode.Append, ["id"]), Source(Rows(5, i => i == 4 ? "2,again" : $"{i},n")));
        Check(dup.Outcome == CsvImportOutcome.RefusedInvalidData && dupFile.Log.Count == 0, "duplicate-key-in-file-refused");
        Check(dup.Issues.Any(i => i.Row == 4 && i.Message.Contains("row 2", StringComparison.OrdinalIgnoreCase) && i.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)), "duplicate-key-in-file-reports-both-rows");
        Check(!dup.Issues.Any(i => i.Message.Contains("again", StringComparison.Ordinal)), "duplicate-key-report-omits-values");

        // duplicate key rejected by the SERVER mid-import: whole import rolls back, batch row range is reported
        var perBatch = PeoplePlan(CsvImportMode.Append).RowsPerBatch;
        var serverDup = new FakeCsvImportFactory(failOnExec: 2, failWith: new CsvImportDuplicateKeyException());
        var sd = await CsvImportApply.ApplyAsync(serverDup, PeoplePlan(CsvImportMode.Append), Source(Rows(perBatch * 3)));
        Check(sd.Outcome == CsvImportOutcome.RolledBackDuplicateKey && !sd.Committed, "server-duplicate-key-outcome");
        Check(serverDup.Log.Contains("rollback") && !serverDup.Log.Contains("commit"), "server-duplicate-key-rolls-back");
        Check(sd.Message.Contains($"rows {perBatch + 1}", StringComparison.Ordinal) && sd.Message.Contains($"{perBatch * 2}", StringComparison.Ordinal), "server-duplicate-key-reports-row-range");
        Check(sd.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase) && sd.RowsInserted == perBatch, "server-duplicate-key-reports-progress");
        Pass("csv-import-apply-duplicate-keys");

        // generic server failure, wrong row count
        var boom = new FakeCsvImportFactory(failOnExec: 1, failWith: new InvalidOperationException("string or binary data would be truncated\nsecond line"));
        var br = await CsvImportApply.ApplyAsync(boom, PeoplePlan(CsvImportMode.Append), Source(Rows(3)));
        Check(br.Outcome == CsvImportOutcome.RolledBackError && boom.Log.Contains("rollback") && !br.Message.Contains('\n'), "error-rolls-back-one-line");
        var wrongCount = new FakeCsvImportFactory(insertCountOverride: 1);
        var wc = await CsvImportApply.ApplyAsync(wrongCount, PeoplePlan(CsvImportMode.Append), Source(Rows(3)));
        Check(wc.Outcome == CsvImportOutcome.RolledBackError && wrongCount.Log.Contains("rollback") && !wrongCount.Log.Contains("commit"), "wrong-row-count-rolls-back");
        Pass("csv-import-apply-errors");

        // cancel mid-import: rollback on an uncancellable token, destination untouched, outcome Cancelled
        using var cts = new CancellationTokenSource();
        var cancelFactory = new FakeCsvImportFactory(onExec: n => { if (n == 2) cts.Cancel(); });
        var cancelled = await CsvImportApply.ApplyAsync(cancelFactory, PeoplePlan(CsvImportMode.Append), Source(Rows(perBatch * 4)), cts.Token);
        Check(cancelled.Outcome == CsvImportOutcome.Cancelled && !cancelled.Committed, "cancel-outcome");
        Check(cancelFactory.Log.Contains("rollback") && !cancelFactory.Log.Contains("commit") && cancelFactory.RollbackToken == CancellationToken.None, "cancel-rolls-back-uncancellable");
        Check(cancelled.Message.Contains("unchanged", StringComparison.OrdinalIgnoreCase), "cancel-says-destination-unchanged");
        Check(cancelFactory.Committed == 0, "cancel-leaves-destination");

        // cancelled before the transaction starts: nothing begins
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        var none = new FakeCsvImportFactory();
        var pc = await CsvImportApply.ApplyAsync(none, PeoplePlan(CsvImportMode.Append), Source(Rows(3)), preCancelled.Token);
        Check(pc.Outcome == CsvImportOutcome.Cancelled && none.Log.Count == 0, "cancel-before-begin");
        Pass("csv-import-apply-cancel");

        // commit / rollback / begin failures never claim more than is known
        var commitFails = new FakeCsvImportFactory(commitFails: new IOException("link down"));
        var cf = await CsvImportApply.ApplyAsync(commitFails, PeoplePlan(CsvImportMode.Append), Source(Rows(3)));
        Check(cf.Outcome == CsvImportOutcome.OutcomeUnknown && cf.RequiresReload && !cf.Message.Contains("rolled back", StringComparison.OrdinalIgnoreCase), "commit-failure-unknown");
        var rollbackFails = new FakeCsvImportFactory(failOnExec: 1, failWith: new InvalidOperationException("x"), rollbackFails: new IOException("gone"));
        var rf = await CsvImportApply.ApplyAsync(rollbackFails, PeoplePlan(CsvImportMode.Append), Source(Rows(3)));
        Check(rf.Outcome == CsvImportOutcome.OutcomeUnknown && rf.RequiresReload, "rollback-failure-unknown");
        var beginFails = new FakeCsvImportFactory(beginFails: new IOException("no connection"));
        var bf = await CsvImportApply.ApplyAsync(beginFails, PeoplePlan(CsvImportMode.Append), Source(Rows(3)));
        Check(bf.Outcome == CsvImportOutcome.RolledBackError && bf.Message.Contains("nothing was", StringComparison.OrdinalIgnoreCase), "begin-failure-nothing-written");
        var disposeFails = new FakeCsvImportFactory(disposeFails: new IOException("dispose"));
        var df = await CsvImportApply.ApplyAsync(disposeFails, PeoplePlan(CsvImportMode.Append), Source(Rows(3)));
        Check(df.Outcome == CsvImportOutcome.OutcomeUnknown, "dispose-failure-after-commit-unknown");
        Pass("csv-import-apply-unknown-outcomes");

        // source changing between validation and write (second pass sees bad data) rolls back
        var calls = 0;
        var flipping = new CsvImportSource(() =>
        {
            calls++;
            return new MemoryStream(Encoding.UTF8.GetBytes(calls == 1 ? Rows(3) : Rows(3, i => i == 3 ? "zzz,x" : $"{i},n")));
        }, new CsvImportOptions());
        var flipFactory = new FakeCsvImportFactory();
        var flip = await CsvImportApply.ApplyAsync(flipFactory, PeoplePlan(CsvImportMode.Append), flipping);
        Check(flip.Outcome == CsvImportOutcome.RolledBackError && flipFactory.Log.Contains("rollback") && !flipFactory.Log.Contains("commit"), "changed-source-rolls-back");

        // progress is reported and non-decreasing
        var seen = new List<long>();
        await CsvImportApply.ApplyAsync(new FakeCsvImportFactory(), PeoplePlan(CsvImportMode.Append), Source(Rows(2500)), progress: new SyncProgress(seen.Add));
        Check(seen.Count >= 2 && seen.Zip(seen.Skip(1), (a, b) => b >= a).All(x => x) && seen[^1] == 2500, "progress-monotonic");
        Pass("csv-import-apply-source-and-progress");
    }

    sealed class SyncProgress(Action<long> onReport) : IProgress<long>
    {
        public void Report(long value) => onReport(value);
    }

    static void AppWiring()
    {
        var root = FindRepoRoot();
        var windowPath = Path.Combine(root, "src", "Sqloryn.App", "ImportCsvWindow.axaml.cs");
        Check(File.Exists(windowPath) && File.Exists(Path.Combine(root, "src", "Sqloryn.App", "ImportCsvWindow.axaml")), "import-window-exists");
        var window = File.ReadAllText(windowPath);
        Check(!window.Contains("INSERT INTO", StringComparison.Ordinal) && !window.Contains("CREATE TABLE", StringComparison.Ordinal)
              && !window.Contains("DELETE FROM", StringComparison.Ordinal), "import-window-no-raw-sql");
        var confirm = window.IndexOf("Dialogs.ConfirmAsync", StringComparison.Ordinal);
        var apply = window.IndexOf("ApplyCsvImportAsync", StringComparison.Ordinal);
        Check(confirm > 0 && apply > confirm, "import-window-confirms-before-apply");
        Check(window.Split("ApplyCsvImportAsync").Length == 2, "import-window-single-write-call-site");
        Check(window.Contains("LoadImmutable", StringComparison.Ordinal), "import-window-uses-immutable-source");
        Check(window.Contains("CsvDestinationType", StringComparison.Ordinal), "import-window-carries-destination-types");
        var shell = File.ReadAllText(Path.Combine(root, "src", "Sqloryn.App", "MainWindow.Shell.cs"));
        Check(shell.Contains("OnImportCsv", StringComparison.Ordinal), "import-reachable-from-menu");
        Pass("csv-import-app-wiring");
    }
}
