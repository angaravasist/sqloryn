using System.Globalization;

namespace Sqloryn.Core;

/// <summary>1-based line and column. The column is 1-based within the line.</summary>
public readonly record struct GoToTarget(int Line, int Column);

/// <summary>
/// Go-to-line input parsing and line/offset math over raw document text. Lines end at \n, \r\n or \r; the
/// editor surface uses its own document model for the real caret move, so this is validation plus pure math
/// that can be tested without a UI. Input is untrusted user text.
/// </summary>
public static class GoToLine
{
    /// <summary>Accepts "line" or "line:column" (both 1-based). Rejects empty, non-numeric, zero, negative and out-of-range values.</summary>
    public static bool TryParse(string? input, int lineCount, out GoToTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(input) || lineCount < 1)
            return false;
        var parts = input.Trim().Split(':');
        if (parts.Length > 2 || !TryPositive(parts[0], out var line) || line > lineCount)
            return false;
        var column = 1;
        if (parts.Length == 2 && !TryPositive(parts[1], out column))
            return false;
        target = new GoToTarget(line, column);
        return true;
    }

    /// <summary>One-line message for a rejected go-to-line input.</summary>
    public static string Describe(string? input, int lineCount)
        => TryParse(input, lineCount, out _)
            ? "OK"
            : $"Enter a line number from 1 to {lineCount.ToString(CultureInfo.InvariantCulture)} (optionally line:column).";

    static bool TryPositive(string s, out int value)
        => int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1;

    public static int CountLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                lines++;
            else if (text[i] == '\r')
            {
                lines++;
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
            }
        }
        return lines;
    }

    /// <summary>Offset of the first character of 1-based <paramref name="line"/>, or -1 when the line does not exist.</summary>
    public static int OffsetOfLine(string text, int line)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (line < 1)
            return -1;
        var current = 1;
        if (line == 1)
            return 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            else if (c != '\r' && c != '\n')
                continue;
            current++;
            if (current == line)
                return i + 1;
        }
        return -1;
    }

    /// <summary>1-based line/column of <paramref name="offset"/> (clamped to the text).</summary>
    public static (int Line, int Column) PositionOf(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        offset = Math.Clamp(offset, 0, text.Length);
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                if (i + 1 >= offset)
                    break; // offset sits between \r and \n: still on the same line
                i++;
            }
            else if (c != '\r' && c != '\n')
                continue;
            line++;
            lineStart = i + 1;
        }
        return (line, offset - lineStart + 1);
    }
}
