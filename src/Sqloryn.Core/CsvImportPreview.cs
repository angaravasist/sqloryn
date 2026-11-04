using System.Globalization;
using System.Text;

namespace Sqloryn.Core;

public sealed record CsvImportOptions
{
    /// <summary>Field delimiter. Never a quote, line break or NUL.</summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>True when the first record holds column names.</summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>
    /// Text encoding. Null means UTF-8, or UTF-16 when a byte-order mark says so. Whatever is chosen is decoded strictly:
    /// invalid bytes are an error, never silently replaced.
    /// </summary>
    public Encoding? Encoding { get; init; }

    /// <summary>How many data rows the preview keeps for display (0-1000).</summary>
    public int SampleRowCount { get; init; } = 20;

    /// <summary>How many data rows the preview reads to guess types and widths (1-1,000,000).</summary>
    public int ScanRowLimit { get; init; } = 10_000;

    internal void Validate()
    {
        if (Delimiter is '"' or '\r' or '\n' or '\0')
            throw new ArgumentException("The delimiter cannot be a quote, a line break or NUL.", nameof(Delimiter));
        if (SampleRowCount is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(SampleRowCount), "Sample rows must be 0-1000.");
        if (ScanRowLimit is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(ScanRowLimit), "Scan limit must be 1-1,000,000 rows.");
    }
}

public enum CsvIssueSeverity { Warning, Error }

/// <param name="Row">1-based data row (the header is not counted), or null for file-level and header issues.</param>
/// <param name="Line">1-based physical line where the record starts, when known.</param>
/// <param name="Column">0-based source column, when the issue belongs to one.</param>
public sealed record CsvImportIssue(CsvIssueSeverity Severity, int? Row, int? Line, int? Column, string Message);

/// <param name="HeaderWasEmpty">The header cell was blank, so <paramref name="Name"/> was generated.</param>
/// <param name="MaxLength">Longest cell seen, in UTF-16 code units, over the scanned rows.</param>
/// <param name="NullOrEmptyCount">Cells that were empty (imported as NULL for non-text columns).</param>
/// <param name="FormulaLikeCount">Cells a spreadsheet could read as a formula; stored verbatim as data.</param>
/// <param name="MaxScale">Most fractional digits seen (decimal guesses only).</param>
public sealed record CsvColumnInfo(
    int Index,
    string Name,
    bool HeaderWasEmpty,
    CsvSqlType GuessedType,
    int MaxLength,
    int NullOrEmptyCount,
    int FormulaLikeCount,
    int MaxScale);

public sealed class CsvImportPreviewResult
{
    internal CsvImportPreviewResult(
        IReadOnlyList<CsvColumnInfo> columns,
        IReadOnlyList<IReadOnlyList<string>> sampleRows,
        int rowsScanned,
        bool scanTruncated,
        IReadOnlyList<CsvImportIssue> issues,
        int errorCount,
        bool hasHeader)
    {
        Columns = columns;
        SampleRows = sampleRows;
        RowsScanned = rowsScanned;
        ScanTruncated = scanTruncated;
        Issues = issues;
        ErrorCount = errorCount;
        HasHeader = hasHeader;
    }

    public IReadOnlyList<CsvColumnInfo> Columns { get; }
    public IReadOnlyList<IReadOnlyList<string>> SampleRows { get; }

    /// <summary>Data rows read for type guessing. Equals the file's row count only when <see cref="ScanTruncated"/> is false.</summary>
    public int RowsScanned { get; }

    /// <summary>True when the file has more rows than were scanned; guesses and widths may not cover them.</summary>
    public bool ScanTruncated { get; }

    public IReadOnlyList<CsvImportIssue> Issues { get; }
    public int ErrorCount { get; }
    public bool HasErrors => ErrorCount > 0;
    public bool HasHeader { get; }
}

/// <summary>
/// Read-only preview of a delimited text file: columns, sample rows, type guesses and problems. It only ever reads its
/// source; nothing is written to the file system or a database. All content is untrusted data.
/// </summary>
public static class CsvImportPreview
{
    internal const int MaxIssues = 100;
    internal const int MaxFieldChars = 1_048_576;

    public static CsvImportPreviewResult Parse(string path, CsvImportOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        return Parse(stream, options);
    }

    public static CsvImportPreviewResult Parse(Stream stream, CsvImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var issues = new List<CsvImportIssue>();
        var errors = 0;
        void Add(CsvIssueSeverity severity, int? row, int? line, int? column, string message)
        {
            if (severity == CsvIssueSeverity.Error)
                errors++;
            if (issues.Count < MaxIssues)
                issues.Add(new CsvImportIssue(severity, row, line, column, message));
        }

        var names = new List<string>();
        var headerEmpty = new List<bool>();
        List<ColumnScan>? scans = null;
        var samples = new List<IReadOnlyList<string>>();
        var rows = 0;
        var truncated = false;
        var width = 0;

        try
        {
            using var records = new CsvRecords(stream, options);
            var fields = new List<string>();

            if (!records.Next(fields, out var line, out var problem))
            {
                Add(CsvIssueSeverity.Error, null, null, null, "The file is empty.");
            }
            else
            {
                if (problem is not null)
                    Add(CsvIssueSeverity.Error, null, line, null, problem);
                width = fields.Count;
                if (options.HasHeader)
                {
                    BuildHeader(fields, names, headerEmpty, Add);
                }
                else
                {
                    for (var i = 0; i < width; i++)
                    {
                        names.Add("Column" + (i + 1).ToString(CultureInfo.InvariantCulture));
                        headerEmpty.Add(false);
                    }
                }

                scans = names.Select(_ => new ColumnScan()).ToList();
                var pending = !options.HasHeader;   // the first record is data when there is no header

                while (true)
                {
                    if (!pending && !records.Next(fields, out line, out problem))
                        break;
                    pending = false;
                    if (rows >= options.ScanRowLimit)
                    {
                        truncated = true;
                        break;
                    }

                    rows++;
                    if (problem is not null)
                        Add(CsvIssueSeverity.Error, rows, line, null, problem);
                    if (fields.Count != width)
                    {
                        Add(CsvIssueSeverity.Error, rows, line, null,
                            $"Row {rows} has {fields.Count} field(s) but the header has {width}.");
                        continue;
                    }

                    for (var c = 0; c < width; c++)
                        scans[c].Add(fields[c]);
                    if (samples.Count < options.SampleRowCount)
                        samples.Add(fields.ToArray());
                }

                if (rows == 0 && errors == 0)
                    Add(CsvIssueSeverity.Warning, null, null, null, "The file has a header but no data rows.");
            }
        }
        catch (DecoderFallbackException)
        {
            Add(CsvIssueSeverity.Error, null, null, null,
                "The file is not valid text in the chosen encoding (invalid bytes were found). Pick the file's real encoding; nothing was replaced or guessed.");
        }
        catch (CsvFormatException ex)
        {
            Add(CsvIssueSeverity.Error, null, ex.Line, null, ex.Message);
        }

        var columns = new List<CsvColumnInfo>(names.Count);
        for (var c = 0; c < names.Count; c++)
        {
            var scan = scans?[c] ?? new ColumnScan();
            var type = scan.Guess();
            columns.Add(new CsvColumnInfo(c, names[c], headerEmpty[c], type, scan.MaxLength, scan.EmptyCount, scan.FormulaLikeCount,
                type == CsvSqlType.Decimal ? scan.MaxScale : 0));
            if (scan.FormulaLikeCount > 0)
                Add(CsvIssueSeverity.Warning, null, null, c,
                    $"Column '{names[c]}' has {scan.FormulaLikeCount} cell(s) starting with = + - @ or a tab (formula-like). " +
                    "They are stored as plain text and never evaluated here, but a spreadsheet may run them if the data is exported without the spreadsheet-safe option.");
        }

        if (errors > issues.Count(i => i.Severity == CsvIssueSeverity.Error))
            issues.Add(new CsvImportIssue(CsvIssueSeverity.Warning, null, null, null,
                $"{errors - issues.Count(i => i.Severity == CsvIssueSeverity.Error)} more problem(s) were not listed."));

        return new CsvImportPreviewResult(columns, samples, rows, truncated, issues, errors, options.HasHeader);
    }

    static void BuildHeader(
        List<string> fields,
        List<string> names,
        List<bool> headerEmpty,
        Action<CsvIssueSeverity, int?, int?, int?, string> add)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < fields.Count; i++)
        {
            var raw = fields[i].Trim();
            var empty = raw.Length == 0;
            var name = empty ? "Column" + (i + 1).ToString(CultureInfo.InvariantCulture) : raw;
            if (empty)
                add(CsvIssueSeverity.Warning, null, null, i, $"Header cell {i + 1} is empty; the column is named '{name}'.");

            var unique = name;
            for (var n = 2; !used.Add(unique); n++)
                unique = name + "_" + n.ToString(CultureInfo.InvariantCulture);
            if (unique != name)
                add(CsvIssueSeverity.Warning, null, null, i, $"Header '{name}' is repeated; column {i + 1} is named '{unique}'.");
            names.Add(unique);
            headerEmpty.Add(empty);
        }
    }

    sealed class ColumnScan
    {
        bool _bit = true, _int = true, _bigInt = true, _decimal = true, _date = true, _guid = true;
        int _nonEmpty;

        public int MaxLength { get; private set; }
        public int EmptyCount { get; private set; }
        public int FormulaLikeCount { get; private set; }
        public int MaxScale { get; private set; }
        int _maxIntegerDigits;

        public void Add(string cell)
        {
            MaxLength = Math.Max(MaxLength, cell.Length);
            if (CsvValue.IsFormulaLike(cell))
                FormulaLikeCount++;
            var text = cell.Trim();
            if (text.Length == 0)
            {
                EmptyCount++;
                return;
            }

            _nonEmpty++;
            if (_bit && !CsvValue.TryParseBit(text, out _)) _bit = false;
            if (_int && !CsvValue.TryParseInt32(text, strictLeadingZeros: true, out _)) _int = false;
            if (_bigInt && !CsvValue.TryParseInt64(text, strictLeadingZeros: true, out _)) _bigInt = false;
            if (_decimal)
            {
                if (CsvValue.TryParseDecimalText(text, strictLeadingZeros: true, out var digits, out var scale) && scale <= 18)
                {
                    MaxScale = Math.Max(MaxScale, scale);
                    _maxIntegerDigits = Math.Max(_maxIntegerDigits, digits);
                }
                else
                {
                    _decimal = false;
                }
            }
            if (_date && !CsvValue.TryParseDateTimeText(text, out _)) _date = false;
            if (_guid && !CsvValue.TryParseGuid(text, out _)) _guid = false;
        }

        public CsvSqlType Guess()
        {
            if (_nonEmpty == 0) return CsvSqlType.NVarChar;
            if (_bit) return CsvSqlType.Bit;
            if (_int) return CsvSqlType.Int;
            if (_bigInt) return CsvSqlType.BigInt;
            if (_decimal && _maxIntegerDigits + MaxScale <= 38) return CsvSqlType.Decimal;
            if (_date) return CsvSqlType.DateTime2;
            if (_guid) return CsvSqlType.UniqueIdentifier;
            return CsvSqlType.NVarChar;
        }
    }
}

/// <summary>A structural problem that stops reading (for example an oversized field).</summary>
sealed class CsvFormatException(string message, int line) : Exception(message)
{
    public int Line { get; } = line;
}

/// <summary>Strict, streaming reader of delimited records over a stream: RFC 4180 quoting, quoted newlines, any line ending.</summary>
sealed class CsvRecords : IDisposable
{
    readonly StreamReader _reader;
    readonly char _delimiter;
    readonly string _encodingName;
    int _peeked = -2;
    bool _first = true;

    public CsvRecords(Stream stream, CsvImportOptions options)
    {
        _delimiter = options.Delimiter;
        Encoding encoding;
        Stream source = stream;
        if (options.Encoding is { } chosen)
        {
            encoding = (Encoding)chosen.Clone();
            encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
            encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        }
        else
        {
            var head = new byte[3];
            var read = 0;
            while (read < head.Length)
            {
                var n = stream.Read(head, read, head.Length - read);
                if (n == 0)
                    break;
                read += n;
            }

            var skip = 0;
            if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                encoding = new UTF8Encoding(false, true);
                skip = 3;
            }
            else if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            {
                encoding = new UnicodeEncoding(false, false, true);
                skip = 2;
            }
            else if (read >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            {
                encoding = new UnicodeEncoding(true, false, true);
                skip = 2;
            }
            else
            {
                encoding = new UTF8Encoding(false, true);
            }

            source = new PrefixedStream(head.AsMemory(skip, read - skip).ToArray(), stream);
        }

        _encodingName = encoding.WebName;
        _reader = new StreamReader(source, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true);
    }

    /// <summary>1-based physical line of the next unread character.</summary>
    public int Line { get; private set; } = 1;

    int Peek()
    {
        if (_peeked == -2)
            _peeked = _reader.Read();
        return _peeked;
    }

    int Take()
    {
        var c = Peek();
        _peeked = -2;
        return c;
    }

    bool AtLineBreak()
    {
        var c = Peek();
        return c is '\r' or '\n';
    }

    void ConsumeLineBreak()
    {
        var c = Take();
        if (c == '\r' && Peek() == '\n')
            Take();
        Line++;
    }

    /// <summary>
    /// Reads the next non-blank record into <paramref name="fields"/> (cleared first). Returns false at end of input.
    /// <paramref name="problem"/> describes a malformed record that was still returned best-effort.
    /// </summary>
    public bool Next(List<string> fields, out int startLine, out string? problem)
    {
        fields.Clear();
        problem = null;
        while (AtLineBreak())
            ConsumeLineBreak();
        if (Peek() < 0)
        {
            startLine = Line;
            return false;
        }

        startLine = Line;
        var sb = new StringBuilder();
        var sawNul = false;
        while (true)
        {
            sb.Clear();
            if (Peek() == '"')
            {
                Take();
                var closed = false;
                while (true)
                {
                    var c = Take();
                    if (c < 0)
                        break;
                    if (c == '"')
                    {
                        if (Peek() == '"')
                        {
                            Take();
                            sb.Append('"');
                            continue;
                        }
                        closed = true;
                        break;
                    }
                    if (c == '\0')
                        sawNul = true;
                    sb.Append((char)c);
                    if (c == '\r' && Peek() == '\n')
                    {
                        sb.Append((char)Take());
                        Line++;
                    }
                    else if (c is '\r' or '\n')
                    {
                        Line++;
                    }
                    if (sb.Length > CsvImportPreview.MaxFieldChars)
                        throw new CsvFormatException("A quoted field is longer than 1,048,576 characters; the file is not a normal delimited text file.", startLine);
                }

                if (!closed)
                {
                    problem = $"A quoted field starting on line {startLine} is never closed (missing closing quote).";
                    fields.Add(Clean(sb));
                    return true;
                }

                // Anything between the closing quote and the delimiter is malformed; keep it so no data vanishes.
                while (Peek() >= 0 && Peek() != _delimiter && !AtLineBreak())
                {
                    problem ??= $"Unexpected text after a closing quote on line {Line}.";
                    sb.Append((char)Take());
                }
            }
            else
            {
                while (true)
                {
                    var c = Peek();
                    if (c < 0 || c == _delimiter || c is '\r' or '\n')
                        break;
                    if (c == '\0')
                        sawNul = true;
                    sb.Append((char)Take());
                    if (sb.Length > CsvImportPreview.MaxFieldChars)
                        throw new CsvFormatException("A field is longer than 1,048,576 characters; the file is not a normal delimited text file.", startLine);
                }
            }

            fields.Add(Clean(sb));
            if (Peek() == _delimiter)
            {
                Take();
                continue;
            }
            break;
        }

        if (AtLineBreak())
            ConsumeLineBreak();
        if (sawNul)
            problem ??= $"The record on line {startLine} contains NUL characters; this looks like a binary file, not delimited text.";
        return true;
    }

    string Clean(StringBuilder sb)
    {
        var text = sb.ToString();
        if (_first)
        {
            _first = false;
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text[1..];
        }
        return text;
    }

    public string EncodingName => _encodingName;

    public void Dispose() => _reader.Dispose();

    /// <summary>Replays bytes already consumed while sniffing for a byte-order mark, then continues with the inner stream.</summary>
    sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_offset < prefix.Length)
            {
                var n = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsSpan(_offset, n).CopyTo(buffer);
                _offset += n;
                return n;
            }
            return inner.Read(buffer);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
