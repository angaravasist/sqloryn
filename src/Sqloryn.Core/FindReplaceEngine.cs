using System.Text;

namespace Sqloryn.Core;

public readonly record struct ReplaceAllResult(string Text, int Count);

public static class FindReplaceEngine
{
    /// <summary>Ordinal search from <paramref name="start"/>; wraps to the beginning when nothing follows. Returns -1 when absent.</summary>
    public static int FindNext(string text, string query, int start, bool caseSensitive, bool wrap = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrEmpty(query) || query.Length > text.Length)
            return -1;
        var comparison = Comparison(caseSensitive);
        start = Math.Clamp(start, 0, text.Length);
        var index = text.IndexOf(query, start, comparison);
        if (index >= 0 || !wrap || start == 0)
            return index;
        index = text.IndexOf(query, 0, comparison);
        return index >= 0 && index < start ? index : -1;
    }

    /// <summary>Replaces non-overlapping occurrences left to right. Replacement text is never re-scanned.</summary>
    public static ReplaceAllResult ReplaceAll(string text, string query, string replacement, bool caseSensitive)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(replacement);
        if (string.IsNullOrEmpty(query))
            return new ReplaceAllResult(text, 0);
        var comparison = Comparison(caseSensitive);
        var sb = new StringBuilder(text.Length);
        var position = 0;
        var count = 0;
        while (true)
        {
            var index = text.IndexOf(query, position, comparison);
            if (index < 0)
                break;
            sb.Append(text, position, index - position).Append(replacement);
            position = index + query.Length;
            count++;
        }
        if (count == 0)
            return new ReplaceAllResult(text, 0);
        sb.Append(text, position, text.Length - position);
        return new ReplaceAllResult(sb.ToString(), count);
    }

    static StringComparison Comparison(bool caseSensitive)
        => caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
