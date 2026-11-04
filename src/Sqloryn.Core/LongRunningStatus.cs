using System.Text;

namespace Sqloryn.Core;

/// <summary>
/// Status and Messages text while a command is still running. This is not a completion outcome.
/// Elapsed time is the client stopwatch, the same wording as <see cref="QueryOutcomeFormatter.DescribeClientElapsed"/>.
/// </summary>
public static class LongRunningStatus
{
    /// <summary>Cancel stays beside Run so it can be found before and during a command.</summary>
    public static bool CancelControlVisible => true;

    public static bool CancelControlEnabled(bool commandRunning) => commandRunning;

    public static string Describe(long elapsedMilliseconds)
    {
        if (elapsedMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        return "Running · " + QueryOutcomeFormatter.DescribeClientElapsed(elapsedMilliseconds);
    }

    public static string FormatMessages(long elapsedMilliseconds)
    {
        var elapsed = Describe(elapsedMilliseconds);
        var sb = new StringBuilder();
        sb.AppendLine("Status: Running");
        sb.AppendLine("The command has not finished. This is not a success, failure, or cancellation outcome.");
        sb.AppendLine(elapsed);
        sb.AppendLine("Cancel is available while this command is running. Elapsed time is measured on this computer, not server statistics.");
        return sb.ToString();
    }
}
