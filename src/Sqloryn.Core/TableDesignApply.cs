using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sqloryn.Core;

/// <summary>One owned database transaction for schema DDL. Nothing is durable until <see cref="CommitAsync"/>.</summary>
public interface ISchemaTransaction : IAsyncDisposable
{
    /// <summary>Reads the table's catalog fingerprint inside the transaction (see <see cref="TableDesignFingerprint"/>).</summary>
    Task<string> ReadFingerprintAsync(TableRef table, CancellationToken cancellationToken);

    /// <summary>Runs one DDL statement. Throws on any server error.</summary>
    Task ExecuteAsync(string sql, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

/// <summary>Starts an <see cref="ISchemaTransaction"/>. Implemented over SqlTransaction by <see cref="SqlSession"/>; faked in tests.</summary>
public interface ISchemaTransactionFactory
{
    Task<ISchemaTransaction> BeginAsync(CancellationToken cancellationToken);
}

public enum TableDesignApplyOutcome
{
    /// <summary>Every statement ran and the transaction committed.</summary>
    Committed,

    /// <summary>The batch was refused before anything ran: a statement is not known to be transactional DDL.</summary>
    RefusedNonTransactional,

    /// <summary>The table's catalog no longer matches what the design was based on. Nothing ran; reload the design.</summary>
    RefusedStale,

    /// <summary>A statement, the freshness read or the provider failed before commit; the transaction was rolled back.</summary>
    RolledBackError,

    /// <summary>Commit or rollback could not be confirmed. The schema may or may not have changed; reload before continuing.</summary>
    OutcomeUnknown
}

/// <param name="StatementsExecuted">How many statements completed before the batch stopped.</param>
/// <param name="FailedIndex">Zero-based index of the statement that failed, when known.</param>
/// <param name="Message">Short, already-safe description for the UI.</param>
public sealed record TableDesignApplyResult(TableDesignApplyOutcome Outcome, int StatementsExecuted, int? FailedIndex, string Message)
{
    public bool Committed => Outcome == TableDesignApplyOutcome.Committed;

    /// <summary>True when the caller must reload the table from the server before trusting any local baseline.</summary>
    public bool RequiresReload => Outcome == TableDesignApplyOutcome.OutcomeUnknown;
}

/// <summary>
/// Applies a reviewed set of DDL statements atomically: one transaction, a schema-freshness check before the first
/// statement, and a full rollback if any statement fails. Mixed or unrecognised batches are refused outright rather
/// than half-applied. Never replays a statement.
/// </summary>
public static class TableDesignApply
{
    public static async Task<TableDesignApplyResult> ApplyAsync(
        ISchemaTransactionFactory factory,
        TableRef table,
        IReadOnlyList<TableDesignStatement> statements,
        string expectedFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(statements);
        if (statements.Count == 0)
            throw new ArgumentException("No statements to apply.", nameof(statements));
        if (string.IsNullOrWhiteSpace(expectedFingerprint))
            throw new ArgumentException("A schema fingerprint from the loaded design is required.", nameof(expectedFingerprint));

        for (var i = 0; i < statements.Count; i++)
        {
            var reason = RefusalReason(statements[i]);
            if (reason is not null)
                return new TableDesignApplyResult(TableDesignApplyOutcome.RefusedNonTransactional, 0, i,
                    $"Statement {i + 1} of {statements.Count} was refused: {reason} Nothing was run. " +
                    "Script it separately; mixed batches are never applied.");
        }

        ISchemaTransaction transaction;
        try
        {
            transaction = await factory.BeginAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new TableDesignApplyResult(TableDesignApplyOutcome.RolledBackError, 0, null,
                "Could not start the schema transaction; nothing was changed. " + Describe(ex));
        }

        // Compute the result first, then dispose explicitly: a throwing DisposeAsync must never replace or lose it.
        TableDesignApplyResult result;
        try
        {
            result = await RunAsync(transaction, table, statements, expectedFingerprint, cancellationToken).ConfigureAwait(false);
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
            return result.Outcome == TableDesignApplyOutcome.Committed
                ? result with
                {
                    Outcome = TableDesignApplyOutcome.OutcomeUnknown,
                    Message = $"{result.Message} {note} The change could not be confirmed; reload before continuing."
                }
                : result with { Message = $"{result.Message} {note}" };
        }

        return result;
    }

    static string? RefusalReason(TableDesignStatement statement)
    {
        if (statement is null || string.IsNullOrWhiteSpace(statement.Sql))
            return "the statement is empty.";
        if (!statement.IsTransactional)
            return "it is marked as DDL that cannot run inside a transaction.";
        if (GoBatchSplitter.ContainsSeparator(statement.Sql))
            return "it contains a batch separator.";
        var text = statement.Sql.TrimStart();
        if (!text.StartsWith("ALTER TABLE ", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase))
            return "only ALTER TABLE and CREATE TABLE are recognised as transactional design DDL.";
        return null;
    }

    static async Task<TableDesignApplyResult> RunAsync(
        ISchemaTransaction transaction,
        TableRef table,
        IReadOnlyList<TableDesignStatement> statements,
        string expectedFingerprint,
        CancellationToken cancellationToken)
    {
        var executed = 0;
        TableDesignApplyResult? failure = null;

        try
        {
            var actual = await transaction.ReadFingerprintAsync(table, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expectedFingerprint, StringComparison.Ordinal))
                failure = new TableDesignApplyResult(TableDesignApplyOutcome.RefusedStale, 0, null,
                    "The table definition on the server changed after this design was loaded. Nothing was run. Reload the table and review the new preview.");
        }
        catch (Exception ex)
        {
            failure = new TableDesignApplyResult(TableDesignApplyOutcome.RolledBackError, 0, null,
                "Could not verify the table definition before applying: " + Describe(ex));
        }

        for (var i = 0; i < statements.Count && failure is null; i++)
        {
            try
            {
                await transaction.ExecuteAsync(statements[i].Sql, cancellationToken).ConfigureAwait(false);
                executed++;
            }
            catch (OperationCanceledException)
            {
                failure = new TableDesignApplyResult(TableDesignApplyOutcome.RolledBackError, executed, i,
                    $"Cancelled while applying statement {i + 1} of {statements.Count}.");
            }
            catch (Exception ex)
            {
                failure = new TableDesignApplyResult(TableDesignApplyOutcome.RolledBackError, executed, i,
                    $"Statement {i + 1} of {statements.Count} failed: {Describe(ex)}");
            }
        }

        if (failure is not null)
        {
            // Roll back on a token that cannot itself be cancelled: a cancelled apply must still undo its changes.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new TableDesignApplyResult(TableDesignApplyOutcome.OutcomeUnknown, executed, failure.FailedIndex,
                    $"{failure.Message} Rollback could not be confirmed ({Describe(ex)}); reload the table before continuing.");
            }
            return failure with { Message = failure.Message + " The whole change was rolled back; the table is unchanged." };
        }

        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never claim rollback after a failed commit: the server may have committed before the failure surfaced.
            return new TableDesignApplyResult(TableDesignApplyOutcome.OutcomeUnknown, executed, null,
                $"Commit could not be confirmed ({Describe(ex)}). The change may or may not have been applied; reload the table before continuing.");
        }

        return new TableDesignApplyResult(TableDesignApplyOutcome.Committed, executed, null,
            $"Applied {executed.ToString(CultureInfo.InvariantCulture)} statement(s) in one transaction.");
    }

    static string Describe(Exception ex)
    {
        var oneLine = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }
}

/// <summary>
/// Catalog fingerprint of one table (name, types, nullability, identity, key membership and the object's modify date).
/// Captured when a design is loaded and compared inside the apply transaction, so a design built on a stale
/// definition is refused instead of altering a table somebody else changed.
/// </summary>
public static class TableDesignFingerprint
{
    /// <summary>Fingerprint of a table that does not exist. A "create" design expects this value.</summary>
    public const string Absent = "absent";

    /// <summary>Parameterised catalog query (one <c>@t</c> parameter: the quoted three-part name). Fixed shape, never user text.</summary>
    public static EditCommand BuildQuery(TableRef table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var db = SqlIdentity.QuoteIdentifier(table.Database);
        var sql =
            "SELECT CONVERT(varchar(33), o.modify_date, 126) AS modified, c.column_id, c.name, ty.name AS type_name, " +
            "c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed, " +
            $"(SELECT TOP (1) ic.key_ordinal FROM {db}.sys.indexes i JOIN {db}.sys.index_columns ic " +
            "ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
            "WHERE i.object_id = o.object_id AND i.is_primary_key = 1 AND ic.column_id = c.column_id) AS pk_ordinal\n" +
            $"FROM {db}.sys.objects o\n" +
            $"LEFT JOIN {db}.sys.columns c ON c.object_id = o.object_id\n" +
            $"LEFT JOIN {db}.sys.types ty ON ty.user_type_id = c.user_type_id\n" +
            "WHERE o.object_id = OBJECT_ID(@t) AND o.type = 'U'\n" +
            "ORDER BY c.column_id;";
        return new EditCommand(sql, [new EditParameter("@t", table.Qualified)]);
    }

    /// <summary>Hashes the rows returned by <see cref="BuildQuery"/>. No rows means the table does not exist.</summary>
    public static string FromRows(RawRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Rows.Count == 0)
            return Absent;
        var sb = new StringBuilder();
        foreach (var row in rows.Rows)
        {
            foreach (var value in row)
                sb.Append(value switch
                {
                    null or DBNull => "<null>",
                    bool b => b ? "1" : "0",
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture)
                }).Append('\u001f');
            sb.Append('\u001e');
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
