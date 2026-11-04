using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sqloryn.Core;

/// <summary>One owned database transaction for an import. Nothing is durable until <see cref="CommitAsync"/>.</summary>
public interface ICsvImportTransaction : IAsyncDisposable
{
    /// <summary>Runs one DDL statement (CREATE TABLE). Throws on any server error.</summary>
    Task ExecuteAsync(string sql, CancellationToken cancellationToken);

    /// <summary>Runs one <see cref="EditCommand"/> and returns the row count it reported.</summary>
    Task<int> ExecuteAffectedAsync(EditCommand command, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

/// <summary>Starts an <see cref="ICsvImportTransaction"/>. Implemented over SqlTransaction by <see cref="SqlSession"/>; faked in tests.</summary>
public interface ICsvImportTransactionFactory
{
    Task<ICsvImportTransaction> BeginAsync(CancellationToken cancellationToken);
}

/// <summary>Thrown by a transaction implementation when the server rejected a write for a duplicate key (errors 2601/2627).</summary>
public sealed class CsvImportDuplicateKeyException : Exception
{
    public CsvImportDuplicateKeyException() : base("The server rejected a duplicate key.") { }
    public CsvImportDuplicateKeyException(string message) : base(message) { }
    public CsvImportDuplicateKeyException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Where the import rows come from. Prefer <see cref="LoadImmutable"/> for file imports so validation and apply
/// share one snapshot and the on-disk fingerprint can be re-checked before any write.
/// </summary>
public sealed record CsvImportSource(
    Func<Stream> Open,
    CsvImportOptions Options,
    string? ContentSha256 = null,
    long? ByteLength = null,
    string? SourcePath = null)
{
    /// <summary>Default upper bound for an immutable in-memory snapshot (256 MiB).</summary>
    public const int DefaultMaxBytes = 256 * 1024 * 1024;

    public static CsvImportSource FromFile(string path, CsvImportOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new CsvImportSource(
            () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan),
            options,
            ContentSha256: null,
            ByteLength: null,
            SourcePath: path);
    }

    /// <summary>
    /// Reads the file once into a bounded byte array, records its SHA-256, and returns a source whose
    /// <see cref="Open"/> always yields that snapshot. Apply re-hashes the path on disk before writing and
    /// refuses when the file changed (including same-row-count edits).
    /// </summary>
    public static CsvImportSource LoadImmutable(string path, CsvImportOptions options, int maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(options);
        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "maxBytes must be at least 1.");

        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("The import file was not found.", path);
        if (info.Length > maxBytes)
            throw new InvalidOperationException(
                $"The file is {info.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes; " +
                $"imports are limited to {maxBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes in memory.");

        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new CsvImportSource(
            () => new MemoryStream(bytes, writable: false),
            options,
            ContentSha256: hash,
            ByteLength: bytes.LongLength,
            SourcePath: path);
    }

    /// <summary>SHA-256 (lowercase hex) of the current bytes at <see cref="SourcePath"/>, or null when unreadable.</summary>
    public static string? HashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public enum CsvImportOutcome
{
    /// <summary>Every row was inserted and the transaction committed.</summary>
    Committed,

    /// <summary>The plan is invalid. Nothing ran.</summary>
    RefusedInvalidPlan,

    /// <summary>The file has unreadable, malformed, unconvertible or duplicate-key rows. No transaction was started.</summary>
    RefusedInvalidData,

    /// <summary>The server rejected a duplicate key; the whole import was rolled back.</summary>
    RolledBackDuplicateKey,

    /// <summary>A statement or the provider failed before commit; the whole import was rolled back.</summary>
    RolledBackError,

    /// <summary>The caller cancelled; the transaction was rolled back and the destination is unchanged.</summary>
    Cancelled,

    /// <summary>Commit or rollback could not be confirmed. The destination may or may not have changed; reload before continuing.</summary>
    OutcomeUnknown
}

/// <param name="RowsRead">Data rows found in the file during validation.</param>
/// <param name="RowsInserted">Rows the server accepted before the import stopped (durable only when committed).</param>
/// <param name="RowsDeleted">Rows removed by Replace before the inserts.</param>
/// <param name="Issues">Per-row problems for refused imports.</param>
public sealed record CsvImportResult(
    CsvImportOutcome Outcome,
    long RowsRead,
    long RowsInserted,
    long RowsDeleted,
    IReadOnlyList<CsvImportIssue> Issues,
    string Message)
{
    public bool Committed => Outcome == CsvImportOutcome.Committed;
    public bool RequiresReload => Outcome == CsvImportOutcome.OutcomeUnknown;
}

/// <summary>
/// Applies a reviewed <see cref="CsvImportPlan"/>. Pass 1 reads and converts the whole file without any database work
/// and refuses on the first set of problems; pass 2 streams it into ONE transaction in parameterised batches. Any
/// failure, duplicate key or cancel rolls everything back, so the destination only changes on a confirmed commit.
/// Never replays a batch.
/// </summary>
public static class CsvImportApply
{
    public const int DefaultMaxRows = 1_000_000;
    const int MaxIssues = 50;

    public static async Task<CsvImportResult> ApplyAsync(
        ICsvImportTransactionFactory factory,
        CsvImportPlan plan,
        CsvImportSource source,
        CancellationToken cancellationToken = default,
        IProgress<long>? progress = null,
        int maxRows = DefaultMaxRows)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        source.Options.Validate();

        if (cancellationToken.IsCancellationRequested)
            return Result(CsvImportOutcome.Cancelled, 0, 0, 0, [], "Import cancelled before anything was written. The destination is unchanged.");

        Validation validation;
        try
        {
            validation = await Task.Run(() => Validate(plan, source, maxRows, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(CsvImportOutcome.Cancelled, 0, 0, 0, [], "Import cancelled while checking the file. The destination is unchanged.");
        }

        if (validation.Refusal is { } refusal)
            return refusal;

        // Fingerprint check before any transaction: same-row-count file edits must not reach DELETE/INSERT.
        if (source.ContentSha256 is { Length: > 0 } expected
            && source.SourcePath is { Length: > 0 } path)
        {
            var live = CsvImportSource.HashFile(path);
            if (live is null || !string.Equals(live, expected, StringComparison.OrdinalIgnoreCase))
            {
                return Result(
                    CsvImportOutcome.RefusedInvalidData,
                    validation.Rows,
                    0,
                    0,
                    [new CsvImportIssue(CsvIssueSeverity.Error, null, null, null,
                        "The import file changed after it was loaded (content fingerprint mismatch, including same-row-count edits).")],
                    "The import file changed after it was validated (fingerprint mismatch). Nothing was written.");
            }
        }

        ICsvImportTransaction transaction;
        try
        {
            transaction = await factory.BeginAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(CsvImportOutcome.Cancelled, validation.Rows, 0, 0, [], "Import cancelled before anything was written. The destination is unchanged.");
        }
        catch (Exception ex)
        {
            return Result(CsvImportOutcome.RolledBackError, validation.Rows, 0, 0, [],
                "Could not start the import transaction; nothing was written. " + Describe(ex));
        }

        // Compute the result first, then dispose explicitly: a throwing DisposeAsync must never replace or lose it.
        CsvImportResult result;
        try
        {
            result = await RunAsync(transaction, plan, source, validation.Rows, cancellationToken, progress).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.DisposeAsync().ConfigureAwait(false); } catch { /* original failure wins */ }
            throw;
        }

        try
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var note = $"Transaction cleanup also failed ({Describe(ex)}).";
            return result.Outcome == CsvImportOutcome.Committed
                ? result with
                {
                    Outcome = CsvImportOutcome.OutcomeUnknown,
                    Message = $"{result.Message} {note} The import could not be confirmed; reload before continuing."
                }
                : result with { Message = $"{result.Message} {note}" };
        }

        return result;
    }

    sealed record Validation(long Rows, CsvImportResult? Refusal);

    static Validation Validate(CsvImportPlan plan, CsvImportSource source, int maxRows, CancellationToken ct)
    {
        var issues = new List<CsvImportIssue>();
        var errorCount = 0;
        void Add(int? row, int? line, int? column, string message)
        {
            errorCount++;
            if (issues.Count < MaxIssues)
                issues.Add(new CsvImportIssue(CsvIssueSeverity.Error, row, line, column, message));
        }

        CsvImportResult Refuse(CsvImportOutcome outcome, long rows, string message)
        {
            var more = errorCount > issues.Count ? $" (+{errorCount - issues.Count} more not listed)" : "";
            return Result(outcome, rows, 0, 0, issues, message + more + " Nothing was written.");
        }

        long rows = 0;
        try
        {
            using var stream = source.Open();
            using var records = new CsvRecords(stream, source.Options);
            var fields = new List<string>();

            if (!records.Next(fields, out var line, out var problem))
            {
                Add(null, null, null, "The file is empty.");
                return new Validation(0, Refuse(CsvImportOutcome.RefusedInvalidData, 0, "The file is empty."));
            }

            var width = fields.Count;
            var planIssues = plan.Validate(width).Where(i => i.Severity == CsvIssueSeverity.Error).ToList();
            if (planIssues.Count > 0)
            {
                issues.AddRange(planIssues.Take(MaxIssues));
                errorCount = planIssues.Count;
                return new Validation(0, Refuse(CsvImportOutcome.RefusedInvalidPlan, 0, "The import mapping is not valid."));
            }

            var keyIndexes = plan.KeyColumns
                .Select(k => plan.Mappings.First(m => string.Equals(m.TargetColumn.Trim(), k.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var seenKeys = keyIndexes.Count > 0 ? new Dictionary<string, long>(StringComparer.Ordinal) : null;
            var pending = !source.Options.HasHeader;

            while (true)
            {
                if (!pending && !records.Next(fields, out line, out problem))
                    break;
                pending = false;
                rows++;
                if ((rows & 0x3FF) == 0)
                    ct.ThrowIfCancellationRequested();
                if (rows > maxRows)
                {
                    Add(null, line, null, $"The file has more than {maxRows.ToString("N0", CultureInfo.InvariantCulture)} rows, the most one import will run.");
                    break;
                }
                if (problem is not null)
                    Add((int)Math.Min(rows, int.MaxValue), line, null, problem);
                if (fields.Count != width)
                {
                    Add((int)Math.Min(rows, int.MaxValue), line, null, $"Row {rows} has {fields.Count} field(s) but the file has {width} column(s).");
                    if (errorCount >= MaxIssues) break;
                    continue;
                }

                var rowOk = true;
                foreach (var m in plan.Mappings)
                {
                    if (!CsvImportPlan.TryConvert(fields[m.SourceIndex], m.Type, plan.EmptyTextAsNull, out _, out var error))
                    {
                        Add((int)Math.Min(rows, int.MaxValue), line, m.SourceIndex, $"Row {rows}, column {m.SourceIndex + 1}: the value {error}.");
                        rowOk = false;
                    }
                    else if (m.Destination is { } dest
                             && !CsvDestinationFidelity.TryAccept(fields[m.SourceIndex], dest, plan.EmptyTextAsNull, out var fidelityError))
                    {
                        Add((int)Math.Min(rows, int.MaxValue), line, m.SourceIndex,
                            $"Row {rows}, column {m.SourceIndex + 1}: the value {fidelityError}.");
                        rowOk = false;
                    }
                }

                if (rowOk && seenKeys is not null)
                {
                    var key = KeyOf(fields, keyIndexes, plan.EmptyTextAsNull);
                    if (key is not null && !seenKeys.TryAdd(key, rows))
                    {
                        Add((int)Math.Min(rows, int.MaxValue), line, null,
                            $"Duplicate key: row {rows} repeats the key of row {seenKeys[key]} ({string.Join(", ", plan.KeyColumns)}).");
                    }
                }

                if (errorCount >= MaxIssues)
                    break;
            }
        }
        catch (DecoderFallbackException)
        {
            Add(null, null, null, "The file is not valid text in the chosen encoding.");
            return new Validation(rows, Refuse(CsvImportOutcome.RefusedInvalidData, rows, "The file is not valid text in the chosen encoding."));
        }
        catch (CsvFormatException ex)
        {
            Add(null, ex.Line, null, ex.Message);
            return new Validation(rows, Refuse(CsvImportOutcome.RefusedInvalidData, rows, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Add(null, null, null, "The file could not be read: " + Describe(ex));
            return new Validation(rows, Refuse(CsvImportOutcome.RefusedInvalidData, rows, "The file could not be read."));
        }

        if (errorCount == 0 && rows == 0)
        {
            Add(null, null, null, "The file has no data rows.");
        }

        return errorCount > 0
            ? new Validation(rows, Refuse(CsvImportOutcome.RefusedInvalidData, rows,
                rows == 0 ? "The file has no data rows." : $"The file has {errorCount} problem(s) that must be fixed first."))
            : new Validation(rows, null);
    }

    static string? KeyOf(List<string> fields, List<CsvColumnMapping> keys, bool emptyTextAsNull)
    {
        var parts = new string[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            CsvImportPlan.TryConvert(fields[keys[i].SourceIndex], keys[i].Type, emptyTextAsNull, out var value, out _);
            if (value is null)
                return null;   // NULL keys are the server's decision; they can never collide with each other here
            parts[i] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        return string.Join('\u001f', parts);
    }

    static async Task<CsvImportResult> RunAsync(
        ICsvImportTransaction transaction,
        CsvImportPlan plan,
        CsvImportSource source,
        long rowsRead,
        CancellationToken ct,
        IProgress<long>? progress)
    {
        long inserted = 0;
        long deleted = 0;
        CsvImportResult? failure = null;
        long batchFirst = 0;
        long batchLast = 0;

        try
        {
            foreach (var ddl in plan.GetPrepareStatements())
            {
                ct.ThrowIfCancellationRequested();
                await transaction.ExecuteAsync(ddl, ct).ConfigureAwait(false);
            }
            foreach (var command in plan.GetPrepareCommands())
            {
                ct.ThrowIfCancellationRequested();
                deleted += await transaction.ExecuteAffectedAsync(command, ct).ConfigureAwait(false);
            }

            using var stream = source.Open();
            using var records = new CsvRecords(stream, source.Options);
            var fields = new List<string>();
            var batch = new List<IReadOnlyList<string>>(plan.RowsPerBatch);
            long row = 0;
            var sawFirst = false;
            var width = -1;

            async Task<bool> FlushAsync()
            {
                ct.ThrowIfCancellationRequested();
                batchLast = row;
                batchFirst = row - batch.Count + 1;
                var command = plan.BuildInsert(batch);
                var affected = await transaction.ExecuteAffectedAsync(command, ct).ConfigureAwait(false);
                if (affected != batch.Count)
                {
                    failure = Result(CsvImportOutcome.RolledBackError, rowsRead, inserted, deleted, [],
                        $"The server reported {affected} inserted row(s) for rows {batchFirst}-{batchLast}, expected {batch.Count}.");
                    return false;
                }

                inserted += batch.Count;
                progress?.Report(inserted);
                batch.Clear();
                return true;
            }

            var ok = true;
            while (ok && records.Next(fields, out _, out var problem))
            {
                if (!sawFirst)
                {
                    sawFirst = true;
                    width = fields.Count;
                    if (source.Options.HasHeader)
                        continue;
                }

                row++;
                if (problem is not null || fields.Count != width)
                    throw new CsvImportConversionException(0, 0, $"Row {row} is malformed now.");
                batch.Add(fields.ToArray());
                if (batch.Count == plan.RowsPerBatch)
                    ok = await FlushAsync().ConfigureAwait(false);
            }

            if (ok && batch.Count > 0)
                await FlushAsync().ConfigureAwait(false);

            if (failure is null && rowsRead > 0 && inserted != rowsRead)
                failure = Result(CsvImportOutcome.RolledBackError, rowsRead, inserted, deleted, [],
                    $"The file changed after it was validated: {rowsRead} row(s) were checked but {inserted} were read for writing.");
        }
        catch (Exception ex) when (ct.IsCancellationRequested || ex is OperationCanceledException)
        {
            failure = Result(CsvImportOutcome.Cancelled, rowsRead, inserted, deleted, [],
                $"Import cancelled after {inserted.ToString("N0", CultureInfo.InvariantCulture)} row(s) were sent.");
        }
        catch (CsvImportDuplicateKeyException)
        {
            failure = Result(CsvImportOutcome.RolledBackDuplicateKey, rowsRead, inserted, deleted, [],
                $"The server rejected a duplicate key in rows {batchFirst}-{batchLast}.");
        }
        catch (CsvImportConversionException ex)
        {
            failure = Result(CsvImportOutcome.RolledBackError, rowsRead, inserted, deleted, [],
                $"{Describe(ex)} The file changed since it was validated.");
        }
        catch (Exception ex)
        {
            var where = batchLast > 0 ? $" (rows {batchFirst}-{batchLast})" : "";
            failure = Result(CsvImportOutcome.RolledBackError, rowsRead, inserted, deleted, [],
                $"The import failed{where}: {Describe(ex)}");
        }

        if (failure is not null)
        {
            // Roll back on a token that cannot itself be cancelled: a cancelled import must still undo its writes.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return failure with
                {
                    Outcome = CsvImportOutcome.OutcomeUnknown,
                    Message = $"{failure.Message} Rollback could not be confirmed ({Describe(ex)}); the destination may have changed. Reload before continuing."
                };
            }
            return failure with
            {
                Message = failure.Message + (failure.Outcome == CsvImportOutcome.Cancelled
                    ? " The transaction was rolled back; the destination is unchanged."
                    : " The whole import was rolled back; the destination is unchanged.")
            };
        }

        try
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never claim rollback after a failed commit: the server may have committed before the failure surfaced.
            return Result(CsvImportOutcome.OutcomeUnknown, rowsRead, inserted, deleted, [],
                $"Commit could not be confirmed ({Describe(ex)}). The import may or may not have been applied; reload before continuing.");
        }

        var removed = deleted > 0 ? $" after removing {deleted.ToString("N0", CultureInfo.InvariantCulture)} existing row(s)" : "";
        return Result(CsvImportOutcome.Committed, rowsRead, inserted, deleted, [],
            $"Imported {inserted.ToString("N0", CultureInfo.InvariantCulture)} row(s){removed}.");
    }

    static CsvImportResult Result(
        CsvImportOutcome outcome, long rowsRead, long inserted, long deleted, IReadOnlyList<CsvImportIssue> issues, string message)
        => new(outcome, rowsRead, inserted, deleted, issues, message);

    static string Describe(Exception ex)
    {
        var oneLine = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }
}
