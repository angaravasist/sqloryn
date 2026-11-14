using System.Globalization;

namespace Sqloryn.Core;

/// <summary>One owned database transaction for permission statements. Nothing is durable until <see cref="CommitAsync"/>.</summary>
public interface IPermissionTransaction : IAsyncDisposable
{
    /// <summary>Runs one previewed statement. Throws on any server error.</summary>
    Task ExecuteAsync(string sql, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

/// <summary>Starts an <see cref="IPermissionTransaction"/>. Implemented over SqlTransaction by <see cref="SqlSession"/>; faked in tests.</summary>
public interface IPermissionTransactionFactory
{
    Task<IPermissionTransaction> BeginAsync(CancellationToken cancellationToken);
}

public enum PermissionApplyOutcome
{
    /// <summary>Every statement ran and the transaction committed.</summary>
    Committed,

    /// <summary>The server refused a statement because the connected login may not change these permissions. Everything was rolled back.</summary>
    RefusedDenied,

    /// <summary>A statement, begin or cancel failed before commit; the transaction was rolled back and permissions are unchanged.</summary>
    RolledBackError,

    /// <summary>Commit or rollback could not be confirmed. Permissions may or may not have changed; reload before continuing.</summary>
    OutcomeUnknown
}

/// <param name="StatementsExecuted">How many statements completed before the batch stopped.</param>
/// <param name="FailedIndex">Zero-based index of the statement that failed, when known.</param>
/// <param name="Message">Short, already-safe, redacted description for the UI.</param>
public sealed record PermissionApplyResult(PermissionApplyOutcome Outcome, int StatementsExecuted, int? FailedIndex, string Message)
{
    public bool Committed => Outcome == PermissionApplyOutcome.Committed;

    /// <summary>True when the caller must re-read permissions from the server before trusting any local view.</summary>
    public bool RequiresReload => Outcome == PermissionApplyOutcome.OutcomeUnknown;
}

/// <summary>
/// Applies a reviewed <see cref="PermissionChangePreview"/> atomically: one transaction, a full rollback if any statement
/// fails, and the server (not the client) decides whether this login may change the permission. Only text from a preview is
/// accepted, so nothing but the reviewed GRANT/DENY/REVOKE can run. Never replays a statement.
/// </summary>
/// <remarks>
/// GRANT/DENY/REVOKE set an explicit state on a principal, so re-running an old preview is idempotent; this layer does not
/// compare the permission state to what the preview was built against. Role-inherited effective permissions are not evaluated.
/// </remarks>
public static class PermissionChangeApply
{
    public static async Task<PermissionApplyResult> ApplyAsync(
        IPermissionTransactionFactory factory, PermissionChangePreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.HasChanges)
            throw new ArgumentException("The preview has no statements to apply.", nameof(preview));

        IPermissionTransaction transaction;
        try
        {
            transaction = await factory.BeginAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new PermissionApplyResult(PermissionApplyOutcome.RolledBackError, 0, null,
                "Could not start the permission transaction; nothing was changed. " + Describe(ex));
        }

        // Compute the result first, then dispose explicitly: a throwing DisposeAsync must never replace or lose it.
        PermissionApplyResult result;
        try
        {
            result = await RunAsync(transaction, preview.Statements, cancellationToken).ConfigureAwait(false);
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
            return result.Outcome == PermissionApplyOutcome.Committed
                ? result with
                {
                    Outcome = PermissionApplyOutcome.OutcomeUnknown,
                    Message = $"{result.Message} {note} The change could not be confirmed; reload before continuing."
                }
                : result with { Message = $"{result.Message} {note}" };
        }

        return result;
    }

    static async Task<PermissionApplyResult> RunAsync(
        IPermissionTransaction transaction, IReadOnlyList<PermissionStatement> statements, CancellationToken cancellationToken)
    {
        var executed = 0;
        PermissionApplyResult? failure = null;

        for (var i = 0; i < statements.Count && failure is null; i++)
        {
            try
            {
                await transaction.ExecuteAsync(statements[i].Sql, cancellationToken).ConfigureAwait(false);
                executed++;
            }
            catch (OperationCanceledException)
            {
                failure = new PermissionApplyResult(PermissionApplyOutcome.RolledBackError, executed, i,
                    $"Cancelled before statement {i + 1} of {statements.Count} finished.");
            }
            catch (Exception ex)
            {
                var number = SecurityFailure.NumberOf(ex);
                failure = SecurityFailure.IsDenied(number)
                    ? new PermissionApplyResult(PermissionApplyOutcome.RefusedDenied, executed, i,
                        $"The server refused statement {i + 1} of {statements.Count}: this login is not allowed to change these permissions (SQL error {number.ToString(CultureInfo.InvariantCulture)}).")
                    : new PermissionApplyResult(PermissionApplyOutcome.RolledBackError, executed, i,
                        $"Statement {i + 1} of {statements.Count} failed: {Describe(ex)}");
            }
        }

        if (failure is not null)
        {
            try
            {
                // Rollback must not be skipped because the caller cancelled.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new PermissionApplyResult(PermissionApplyOutcome.OutcomeUnknown, executed, failure.FailedIndex,
                    $"{failure.Message} Rollback could not be confirmed ({Describe(ex)}); reload permissions before continuing.");
            }
            return failure with { Message = failure.Message + " The whole change was rolled back; permissions are unchanged." };
        }

        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never claim rollback after a failed commit: the server may have committed before the failure surfaced.
            return new PermissionApplyResult(PermissionApplyOutcome.OutcomeUnknown, executed, null,
                $"Commit could not be confirmed ({Describe(ex)}). The change may or may not have been applied; reload permissions before continuing.");
        }

        return new PermissionApplyResult(PermissionApplyOutcome.Committed, executed, null,
            $"Applied {executed.ToString(CultureInfo.InvariantCulture)} statement(s) in one transaction.");
    }

    static string Describe(Exception ex)
    {
        var oneLine = SchemaObjectCatalog.OneLine(DiagnosticsRedactor.Redact(ex.Message));
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }
}
