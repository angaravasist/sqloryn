namespace Sqloryn.Core;

public static class GoBatchSplitter
{
    public const int MaxRepeatCount = 1_000;
    public const int MaxTotalBatches = 1_000;

    enum LexState
    {
        Normal,
        SingleQuote,
        DoubleQuote,
        Bracket,
        BlockComment
    }

    public static IReadOnlyList<string> Split(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var batches = new List<string>();
        Scan(sql, (text, count) =>
        {
            for (var i = 0; i < count; i++)
            {
                if (batches.Count >= MaxTotalBatches)
                    throw new ArgumentException($"Script expands to more than {MaxTotalBatches} batches.");
                batches.Add(text);
            }
        }, out _);
        return batches;
    }

    /// <summary>True when at least one line outside strings/comments is a GO separator.</summary>
    public static bool ContainsSeparator(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        Scan(sql, static (_, _) => { }, out var found);
        return found;
    }

    static void Scan(string sql, Action<string, int> emit, out bool foundSeparator)
    {
        foundSeparator = false;
        var state = LexState.Normal;
        var blockDepth = 0;
        var batchStart = 0;
        var pos = 0;

        void Flush(int end, int count)
        {
            var text = sql.AsSpan(batchStart, end - batchStart).Trim().ToString();
            if (text.Length > 0)
                emit(text, count);
        }

        while (pos < sql.Length)
        {
            var lineEnd = pos;
            while (lineEnd < sql.Length && sql[lineEnd] is not ('\r' or '\n'))
                lineEnd++;
            var next = lineEnd;
            if (next < sql.Length)
                next += sql[next] == '\r' && next + 1 < sql.Length && sql[next + 1] == '\n' ? 2 : 1;

            var line = sql.AsSpan(pos, lineEnd - pos);
            if (state == LexState.Normal && TryParseGoLine(line, out var count))
            {
                foundSeparator = true;
                Flush(pos, count);
                batchStart = next;
                pos = next;
                continue;
            }

            ScanLine(line, ref state, ref blockDepth);
            pos = next;
        }

        Flush(sql.Length, 1);
    }

    static void ScanLine(ReadOnlySpan<char> line, ref LexState state, ref int blockDepth)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            var nextCh = i + 1 < line.Length ? line[i + 1] : '\0';
            switch (state)
            {
                case LexState.Normal:
                    if (ch == '\'')
                        state = LexState.SingleQuote;
                    else if (ch == '"')
                        state = LexState.DoubleQuote;
                    else if (ch == '[')
                        state = LexState.Bracket;
                    else if (ch == '-' && nextCh == '-')
                        return;
                    else if (ch == '/' && nextCh == '*')
                    {
                        state = LexState.BlockComment;
                        blockDepth = 1;
                        i++;
                    }
                    break;
                case LexState.SingleQuote:
                    if (ch == '\'')
                    {
                        if (nextCh == '\'')
                            i++;
                        else
                            state = LexState.Normal;
                    }
                    break;
                case LexState.DoubleQuote:
                    if (ch == '"')
                    {
                        if (nextCh == '"')
                            i++;
                        else
                            state = LexState.Normal;
                    }
                    break;
                case LexState.Bracket:
                    if (ch == ']')
                    {
                        if (nextCh == ']')
                            i++;
                        else
                            state = LexState.Normal;
                    }
                    break;
                case LexState.BlockComment:
                    if (ch == '/' && nextCh == '*')
                    {
                        blockDepth++;
                        i++;
                    }
                    else if (ch == '*' && nextCh == '/')
                    {
                        blockDepth--;
                        i++;
                        if (blockDepth == 0)
                            state = LexState.Normal;
                    }
                    break;
            }
        }
    }

    static bool TryParseGoLine(ReadOnlySpan<char> line, out int count)
    {
        count = 1;
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || !trimmed[..2].Equals("GO", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = trimmed[2..];
        if (rest.Length == 0)
            return true;
        if (!char.IsWhiteSpace(rest[0]))
            return false;
        rest = rest.TrimStart();
        if (rest.Length == 0 || rest.StartsWith("--", StringComparison.Ordinal))
            return true;

        var digits = 0;
        while (digits < rest.Length && rest[digits] is >= '0' and <= '9')
            digits++;
        if (digits == 0)
            return false;
        var tail = rest[digits..].TrimStart();
        if (tail.Length > 0 && !tail.StartsWith("--", StringComparison.Ordinal))
            return false;
        if (!int.TryParse(rest[..digits], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
            parsed is < 1 or > MaxRepeatCount)
            throw new ArgumentException($"GO repeat count must be between 1 and {MaxRepeatCount}.");
        count = parsed;
        return true;
    }
}
