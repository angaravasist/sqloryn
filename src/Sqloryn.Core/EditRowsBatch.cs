using System.Globalization;

namespace Sqloryn.Core;

/// <summary>One owned database transaction. Commands run in order; nothing is durable until <see cref="CommitAsync"/>.</summary>
public interface IEditTransaction : IAsyncDisposable
{
    /// <summary>Runs one <see cref="EditCommand"/> and returns the dependable affected-row count it reported.</summary>
    Task<int> ExecuteAffectedAsync(EditCommand command, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

/// <summary>Starts an <see cref="IEditTransaction"/>. Implemented over SqlTransaction by <see cref="SqlSession"/>; faked in tests.</summary>
public interface IEditTransactionFactory
{
    Task<IEditTransaction> BeginAsync(CancellationToken cancellationToken);
}

public enum EditBatchOutcome
{
    /// <summary>Every write was validated and the transaction committed.</summary>
    Committed,

    /// <summary>A write matched zero rows (optimistic-concurrency conflict); the whole set was rolled back.</summary>
    RolledBackConflict,

    /// <summary>A write reported something other than exactly one row; the whole set was rolled back.</summary>
    RolledBackWrongRowCount,

    /// <summary>A provider/cancellation error occurred before commit; the whole set was rolled back.</summary>
    RolledBackError,

    /// <summary>Commit or rollback could not be confirmed. The data may or may not have changed; reload before editing.</summary>
    OutcomeUnknown
}

/// <param name="Outcome">Final state of the set.</param>
/// <param name="WritesValidated">How many writes had a validated row count before the batch stopped.</param>
/// <param name="FailedIndex">Zero-based index of the command that stopped the batch, when known.</param>
/// <param name="Message">Short, already-safe description for the UI.</param>
public sealed record EditBatchResult(EditBatchOutcome Outcome, int WritesValidated, int? FailedIndex, string Message)
{
    public bool Committed => Outcome == EditBatchOutcome.Committed;

    /// <summary>True when the caller must reload data from the server before trusting any local baseline.</summary>
    public bool RequiresReload => Outcome == EditBatchOutcome.OutcomeUnknown;
}

/// <summary>
/// Applies a set of keyed edits atomically: one transaction, every write's row count validated before commit,
/// and any failure rolls the entire set back. Never replays a command.
/// </summary>
public static class EditRowsBatch
{
    public static async Task<EditBatchResult> ApplyAsync(
        IEditTransactionFactory factory,
        IReadOnlyList<EditCommand> commands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
            throw new ArgumentException("No edits to apply.", nameof(commands));

        IEditTransaction transaction;
        try
        {
            transaction = await factory.BeginAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new EditBatchResult(EditBatchOutcome.RolledBackError, 0, null,
                "Could not start the edit transaction; nothing was written. " + Describe(ex));
        }

        await using (transaction.ConfigureAwait(false))
        {
            var validated = 0;
            EditBatchResult? failure = null;
            for (var i = 0; i < commands.Count && failure is null; i++)
            {
                int affected;
                try
                {
                    affected = await transaction.ExecuteAffectedAsync(commands[i], cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    failure = new EditBatchResult(EditBatchOutcome.RolledBackError, validated, i,
                        $"Cancelled while applying edit {i + 1} of {commands.Count}.");
                    break;
                }
                catch (Exception ex)
                {
                    failure = new EditBatchResult(EditBatchOutcome.RolledBackError, validated, i,
                        $"Edit {i + 1} of {commands.Count} failed: {Describe(ex)}");
                    break;
                }

                if (affected == 0)
                {
                    failure = new EditBatchResult(EditBatchOutcome.RolledBackConflict, validated, i,
                        $"Edit {i + 1} of {commands.Count} conflicted: the row was changed or deleted by someone else, or no longer matches.");
                }
                else if (affected != 1)
                {
                    failure = new EditBatchResult(EditBatchOutcome.RolledBackWrongRowCount, validated, i,
                        $"Edit {i + 1} of {commands.Count} reported {affected.ToString(CultureInfo.InvariantCulture)} affected rows; the key did not identify a single row.");
                }
                else
                {
                    validated++;
                }
            }

            if (failure is not null)
            {
                // Roll back on a token that cannot itself be cancelled: a cancelled save must still undo its writes.
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new EditBatchResult(EditBatchOutcome.OutcomeUnknown, validated, failure.FailedIndex,
                        $"{failure.Message} Rollback could not be confirmed ({Describe(ex)}); reload before editing again.");
                }
                return failure with
                {
                    Message = failure.Message + " The whole set was rolled back; no row was written."
                };
            }

            try
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never claim rollback after a failed commit: the server may have committed before the failure surfaced.
                return new EditBatchResult(EditBatchOutcome.OutcomeUnknown, validated, null,
                    $"Commit could not be confirmed ({Describe(ex)}). The edits may or may not have been saved; reload before editing again.");
            }

            return new EditBatchResult(EditBatchOutcome.Committed, validated, null,
                $"Saved {validated.ToString(CultureInfo.InvariantCulture)} row(s) in one transaction.");
        }
    }

    static string Describe(Exception ex)
    {
        var oneLine = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }
}
