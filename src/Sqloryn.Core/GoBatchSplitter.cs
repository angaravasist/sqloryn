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

    /// <summary>
    /// Compatibility path: expands repeat counts into plain trimmed batch texts.
    /// Throws <see cref="SqlScriptRejectedException"/> (an <see cref="ArgumentException"/>) for rejected scripts.
    /// </summary>
    public static IReadOnlyList<string> Split(string sql)
    {
        var script = Parse(sql);
        var batches = new List<string>(script.ExpandedBatchCount);
        foreach (var batch in script.Batches)
            for (var i = 0; i < batch.RepeatCount; i++)
                batches.Add(batch.Text);
        return batches;
    }

    /// <summary>
    /// Preflight parse of the whole script. Returns batches with source spans, or throws
    /// <see cref="SqlScriptRejectedException"/> (with line/offset) for SQLCMD directives, malformed GO lines,
    /// or repeat counts beyond the bounds. Nothing is executed here.
    /// </summary>
    public static SqlScript Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var batches = new List<SqlBatch>();
        var expanded = 0;
        var hasSeparator = false;
        Scan(sql, (batch, location) =>
        {
            expanded += batch.RepeatCount;
            if (expanded > MaxTotalBatches)
                throw new SqlScriptRejectedException(SqlScriptRejectionKind.TooManyBatches,
                    $"Script expands to more than {MaxTotalBatches} batches.", location);
            batches.Add(batch);
        }, out hasSeparator);
        return new SqlScript(batches, hasSeparator, expanded);
    }

    /// <summary>True when at least one line outside strings/comments is a GO separator.</summary>
    public static bool ContainsSeparator(string sql) => Parse(sql).HasSeparator;

    static void Scan(string sql, Action<SqlBatch, SqlScriptLocation> emit, out bool foundSeparator)
    {
        foundSeparator = false;
        var separatorSeen = false;
        var state = LexState.Normal;
        var blockDepth = 0;
        var batchStart = 0;
        var batchStartLine = 1;
        var pos = 0;
        var lineNo = 1;

        // location = where a rejection caused by this emit is reported.
        void Flush(int end, int count, SqlScriptLocation location)
        {
            var raw = sql.AsSpan(batchStart, end - batchStart);
            var trimmedStart = raw.Length - raw.TrimStart().Length;
            var text = raw.Trim().ToString();
            if (text.Length == 0)
                return;
            var line = batchStartLine;
            for (var i = batchStart; i < batchStart + trimmedStart; i++)
            {
                if (sql[i] == '\n')
                    line++;
                else if (sql[i] == '\r' && !(i + 1 < sql.Length && sql[i + 1] == '\n'))
                    line++;
            }
            emit(new SqlBatch(text, batchStart + trimmedStart, text.Length, line, count), location);
        }

        while (pos < sql.Length)
        {
            var lineEnd = pos;
            while (lineEnd < sql.Length && sql[lineEnd] is not ('\r' or '\n'))
                lineEnd++;
            var next = lineEnd;
            if (next < sql.Length)
                next += sql[next] == '\r' && next + 1 < sql.Length && sql[next + 1] == '\n' ? 2 : 1;

            var lineSpan = sql.AsSpan(pos, lineEnd - pos);
            if (state == LexState.Normal)
            {
                var indent = lineSpan.Length - lineSpan.TrimStart().Length;
                var lineLocation = new SqlScriptLocation(pos + indent, lineNo, indent + 1);
                var trimmedLine = lineSpan.Trim();

                // SQLCMD directives are ":name" or ":!!…". T-SQL scope resolution "::fn_…" must not be rejected.
                if (trimmedLine.Length > 0 && trimmedLine[0] == ':' &&
                    !(trimmedLine.Length > 1 && trimmedLine[1] == ':'))
                {
                    if (trimmedLine.Length == 1 ||
                        trimmedLine.StartsWith(":!!", StringComparison.Ordinal) ||
                        char.IsLetter(trimmedLine[1]))
                    {
                        var nameEnd = 1;
                        while (nameEnd < trimmedLine.Length && !char.IsWhiteSpace(trimmedLine[nameEnd]) && trimmedLine[nameEnd] != '(')
                            nameEnd++;
                        var name = trimmedLine[..nameEnd].ToString();
                        throw new SqlScriptRejectedException(SqlScriptRejectionKind.SqlcmdDirective,
                            $"SQLCMD directive '{name}' is not supported.", lineLocation);
                    }
                }

                var go = ClassifyGoLine(trimmedLine, out var count);
                if (go == GoLine.Malformed)
                    throw new SqlScriptRejectedException(SqlScriptRejectionKind.MalformedGo,
                        "Malformed GO separator. Use 'GO', or 'GO <count>' with an optional -- comment.", lineLocation);
                if (go == GoLine.RepeatOutOfRange)
                    throw new SqlScriptRejectedException(SqlScriptRejectionKind.RepeatCountOutOfRange,
                        $"GO repeat count must be between 1 and {MaxRepeatCount}.", lineLocation);
                if (go == GoLine.Separator)
                {
                    separatorSeen = true;
                    Flush(pos, count, lineLocation);
                    batchStart = next;
                    batchStartLine = lineNo + 1;
                    pos = next;
                    lineNo++;
                    continue;
                }
            }

            ScanLine(lineSpan, ref state, ref blockDepth);
            pos = next;
            lineNo++;
        }

        // Final batch: a rejection here is reported at the batch's own start.
        Flush(sql.Length, 1, new SqlScriptLocation(batchStart, batchStartLine, 1));
        foundSeparator = separatorSeen;
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

    enum GoLine { NotGo, Separator, Malformed, RepeatOutOfRange }

    static GoLine ClassifyGoLine(ReadOnlySpan<char> trimmed, out int count)
    {
        count = 1;
        if (trimmed.Length < 2 || !trimmed[..2].Equals("GO", StringComparison.OrdinalIgnoreCase))
            return GoLine.NotGo;
        var rest = trimmed[2..];
        if (rest.Length == 0)
            return GoLine.Separator;
        if (rest.StartsWith("--", StringComparison.Ordinal))
            return GoLine.Separator;
        if (rest[0] == ';')
            return GoLine.Malformed;
        if (!char.IsWhiteSpace(rest[0]))
            return GoLine.NotGo; // GOTO, go.col, etc.
        rest = rest.TrimStart();
        if (rest.Length == 0 || rest.StartsWith("--", StringComparison.Ordinal))
            return GoLine.Separator;

        var digits = 0;
        while (digits < rest.Length && rest[digits] is >= '0' and <= '9')
            digits++;
        if (digits == 0)
            return GoLine.Malformed;
        var tail = rest[digits..].TrimStart();
        if (tail.Length > 0 && !tail.StartsWith("--", StringComparison.Ordinal))
            return GoLine.Malformed;
        if (!int.TryParse(rest[..digits], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
            parsed is < 1 or > MaxRepeatCount)
            return GoLine.RepeatOutOfRange;
        count = parsed;
        return GoLine.Separator;
    }
}

/// <summary>Source position (0-based UTF-16 offset, 1-based line and column) in the original script.</summary>
public readonly record struct SqlScriptLocation(int Offset, int Line, int Column);

/// <summary>
/// One batch from the original script. <see cref="Start"/>/<see cref="Length"/> span the trimmed
/// <see cref="Text"/> in the source; <see cref="RepeatCount"/> comes from a following <c>GO n</c>.
/// </summary>
public sealed record SqlBatch(string Text, int Start, int Length, int StartLine, int RepeatCount);

/// <summary>Result of a preflight parse. <see cref="ExpandedBatchCount"/> is the sum of repeat counts.</summary>
public sealed record SqlScript(IReadOnlyList<SqlBatch> Batches, bool HasSeparator, int ExpandedBatchCount);

public enum SqlScriptRejectionKind
{
    SqlcmdDirective,
    MalformedGo,
    RepeatCountOutOfRange,
    TooManyBatches
}

/// <summary>Raised by preflight before any batch is sent to the provider. Nothing was executed.</summary>
public sealed class SqlScriptRejectedException : ArgumentException
{
    public SqlScriptRejectionKind Kind { get; }
    /// <summary>Why the script was rejected, without location.</summary>
    public string Reason { get; }
    /// <summary>0-based UTF-16 offset in the document (equals <see cref="SelectionOffset"/> when no origin applies).</summary>
    public int Offset { get; }
    /// <summary>1-based line in the document (equals <see cref="SelectionLine"/> when no origin applies).</summary>
    public int Line { get; }
    /// <summary>1-based column in the document line.</summary>
    public int Column { get; }
    /// <summary>0-based offset relative to the executed text (the selection).</summary>
    public int SelectionOffset { get; }
    /// <summary>1-based line relative to the executed text (the selection).</summary>
    public int SelectionLine { get; }

    public SqlScriptRejectedException(SqlScriptRejectionKind kind, string reason, SqlScriptLocation location)
        : this(kind, reason, location, location)
    {
    }

    SqlScriptRejectedException(SqlScriptRejectionKind kind, string reason, SqlScriptLocation selection, SqlScriptLocation document)
        : base($"Script rejected at line {document.Line}, column {document.Column} (offset {document.Offset}): {reason} Nothing was executed.")
    {
        Kind = kind;
        Reason = reason;
        Offset = document.Offset;
        Line = document.Line;
        Column = document.Column;
        SelectionOffset = selection.Offset;
        SelectionLine = selection.Line;
        _selectionColumn = selection.Column;
    }

    readonly int _selectionColumn;

    /// <summary>
    /// Re-maps this selection-relative rejection to document coordinates:
    /// line = DocumentLineOffset + selectionLine, offset = DocumentOffset + selectionOffset;
    /// the column is shifted only on the selection's first line.
    /// </summary>
    public SqlScriptRejectedException WithOrigin(ScriptExecutionOrigin origin)
    {
        var selection = new SqlScriptLocation(SelectionOffset, SelectionLine, _selectionColumn);
        var document = new SqlScriptLocation(
            checked(origin.DocumentOffset + SelectionOffset),
            checked(origin.DocumentLineOffset + SelectionLine),
            SelectionLine == 1 ? checked(_selectionColumn + origin.DocumentColumnOffset) : _selectionColumn);
        return new SqlScriptRejectedException(Kind, Reason, selection, document);
    }
}

/// <summary>
/// Where the executed text (a selection) starts in the full document, so rejections can be reported
/// against the document. <see cref="DocumentLineOffset"/> is the 0-based line index of the selection start,
/// <see cref="DocumentOffset"/> its 0-based UTF-16 offset and <see cref="DocumentColumnOffset"/> its 0-based column.
/// </summary>
public readonly record struct ScriptExecutionOrigin
{
    public int DocumentLineOffset { get; }
    public int DocumentOffset { get; }
    public int DocumentColumnOffset { get; }

    public ScriptExecutionOrigin(int documentLineOffset, int documentOffset, int documentColumnOffset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(documentLineOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(documentOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(documentColumnOffset);
        DocumentLineOffset = documentLineOffset;
        DocumentOffset = documentOffset;
        DocumentColumnOffset = documentColumnOffset;
    }

    public static ScriptExecutionOrigin None => default;

    /// <summary>
    /// Computes the origin of a selection starting at <paramref name="selectionStart"/> (clamped) in
    /// <paramref name="document"/>. Line breaks are \n, \r\n and lone \r, matching the script scanner.
    /// </summary>
    public static ScriptExecutionOrigin FromDocument(string document, int selectionStart)
    {
        ArgumentNullException.ThrowIfNull(document);
        var start = Math.Clamp(selectionStart, 0, document.Length);
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < start; i++)
        {
            var ch = document[i];
            if (ch == '\n' || (ch == '\r' && !(i + 1 < document.Length && document[i + 1] == '\n')))
            {
                line++;
                lineStart = i + 1;
            }
        }
        return new ScriptExecutionOrigin(line, start, start - lineStart);
    }
}
