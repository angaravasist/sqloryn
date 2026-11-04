namespace Sqloryn.Core;

/// <summary>Runs the single GO batch under a caret or selection. Neighbour batches are not sent.</summary>
public static class GoBatchExecution
{
    public readonly record struct OneBatchPlan(string Sql, int Index, int Count, int RepeatCount, int Start, string Note);

    /// <summary>Selection start wins when characters are selected; otherwise the editor caret.</summary>
    public static int ResolveCaret(int editorCaret, int selectionStart, int selectionLength)
        => selectionLength > 0 ? selectionStart : editorCaret;

    public static OneBatchPlan Plan(string script, int caret)
    {
        var chosen = GoBatchSplitter.Choose(script, caret);
        var batches = GoBatchSplitter.Parse(script).Batches;
        var index = 0;
        for (var i = 0; i < batches.Count; i++)
        {
            if (batches[i].Start == chosen.Start && batches[i].Text == chosen.Text)
            {
                index = i;
                break;
            }
        }
        var count = batches.Count;
        var note = chosen.RepeatCount > 1
            ? $"Running batch {index + 1} of {count} once. GO {chosen.RepeatCount} is not repeated. Other batches were not run."
            : count <= 1
                ? "Running the only batch."
                : $"Running batch {index + 1} of {count}. Other batches were not run.";
        return new OneBatchPlan(chosen.Text, index, count, chosen.RepeatCount, chosen.Start, note);
    }

    public static async Task<QueryResult> ExecuteOneAsync(
        Func<string, CancellationToken, Task<QueryResult>> executeOneBatch,
        string script,
        int caret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executeOneBatch);
        var plan = Plan(script, caret);
        return await executeOneBatch(plan.Sql, cancellationToken).ConfigureAwait(false);
    }
}
