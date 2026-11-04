using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Sqloryn.Core;

/// <summary>Limits for <see cref="SqlSession.QueryRawAsync"/>; a cell is never read past these.</summary>
/// <param name="MaxCellBytes">Largest single string/binary cell that is loaded (strings count 2 bytes per char).</param>
/// <param name="MaxTotalBytes">Total string/binary bytes retained across all rows of one call.</param>
public sealed record RawReadBudget(int MaxCellBytes, long MaxTotalBytes)
{
    public static readonly RawReadBudget Default = new(64 * 1024, 16L * 1024 * 1024);
}

/// <summary>
/// Placeholder for a cell that was not (fully) loaded. It carries at most a short preview for display and is
/// deliberately not a string/byte[]: a shortened value must never be written back or used in an optimistic predicate.
/// </summary>
public sealed record RawCellOmitted(string Reason, string? Preview, long ObservedUnits)
{
    public static bool IsOmitted(object? value) => value is RawCellOmitted;

    public string Display => string.IsNullOrEmpty(Preview) ? $"‹{Reason}›" : $"{Preview}… ‹{Reason}›";
}

/// <summary>Bounded sequential reader behind <see cref="SqlSession.QueryRawAsync"/>.</summary>
internal static class RawRowReader
{
    const int ChunkChars = 2048;
    const int PreviewChars = 128;
    const int PreviewBytes = 16;
    const int FixedCellCharge = 16;

    static readonly HashSet<string> StringTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "varchar", "nchar", "nvarchar", "text", "ntext", "xml"
    };

    static readonly HashSet<string> BinaryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "binary", "varbinary", "image", "timestamp", "rowversion"
    };

    static readonly HashSet<string> UnsupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "geography", "geometry", "hierarchyid"
    };

    public static async Task<RawRows> ReadAsync(
        DbDataReader reader,
        int maxRows,
        RawReadBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.MaxCellBytes < 0 || budget.MaxTotalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(budget));

        var fieldCount = reader.FieldCount;
        var columns = new List<string>(fieldCount);
        var types = new List<string>(fieldCount);
        for (var i = 0; i < fieldCount; i++)
        {
            columns.Add(reader.GetName(i));
            types.Add(reader.GetDataTypeName(i));
        }

        var rows = new List<object?[]>();
        var truncated = false;
        var exhausted = false;
        var hasOmitted = false;
        var remaining = budget.MaxTotalBytes;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count >= maxRows)
            {
                truncated = true;
                break;
            }

            // Sequential access: every ordinal is read once, in order.
            var values = new object?[fieldCount];
            for (var i = 0; i < fieldCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = ReadCell(reader, i, types[i], budget.MaxCellBytes, ref remaining, out var omitted);
                values[i] = value;
                hasOmitted |= omitted;
                if (omitted && remaining <= 0)
                    exhausted = true;
            }
            rows.Add(values);

            if (exhausted)
            {
                // The retained-bytes budget is gone; further rows would only be omitted placeholders.
                truncated = true;
                break;
            }
        }

        return new RawRows(columns, types, rows, truncated, exhausted, hasOmitted);
    }

    static object? ReadCell(
        DbDataReader reader, int ordinal, string type, int cellCap, ref long remaining, out bool omitted)
    {
        omitted = false;
        if (UnsupportedTypes.Contains(type) || type.Contains('.', StringComparison.Ordinal))
        {
            omitted = true;
            return new RawCellOmitted($"{type} not loaded", null, -1);
        }

        if (reader.IsDBNull(ordinal))
            return null;

        if (StringTypes.Contains(type))
            return ReadString(reader, ordinal, cellCap, ref remaining, out omitted);
        if (BinaryTypes.Contains(type))
            return ReadBinary(reader, ordinal, cellCap, ref remaining, out omitted);

        if ((type.Equals("decimal", StringComparison.OrdinalIgnoreCase) || type.Equals("numeric", StringComparison.OrdinalIgnoreCase))
            && reader is SqlDataReader sqlReader)
        {
            // numeric(29..38) does not fit a CLR decimal; never read it through GetValue (it throws or loses digits).
            var sd = sqlReader.GetSqlDecimal(ordinal);
            if (sd.Precision > 28)
            {
                omitted = true;
                return new RawCellOmitted("high-precision numeric not loaded", sd.ToString(), -1);
            }
            return sd.Value;
        }

        remaining -= FixedCellCharge;
        return reader.GetValue(ordinal);
    }

    static object ReadString(DbDataReader reader, int ordinal, int cellCap, ref long remaining, out bool omitted)
    {
        omitted = false;
        var allowedChars = (int)Math.Min(cellCap, Math.Max(0, remaining)) / 2;
        var sb = new StringBuilder();
        var buffer = new char[ChunkChars];
        long offset = 0;
        // Read at most allowedChars + 1 so "exactly at the cap" is distinguishable from "over the cap".
        while (sb.Length <= allowedChars)
        {
            var want = (int)Math.Min(buffer.Length, allowedChars + 1 - sb.Length);
            var read = (int)reader.GetChars(ordinal, offset, buffer, 0, want);
            if (read <= 0)
                break;
            sb.Append(buffer, 0, read);
            offset += read;
        }

        if (sb.Length <= allowedChars)
        {
            remaining -= (long)sb.Length * 2 + FixedCellCharge;
            return sb.ToString();
        }

        omitted = true;
        var preview = sb.ToString(0, Math.Min(PreviewChars, sb.Length));
        remaining -= (long)allowedChars * 2;
        // Observed is a lower bound only; the true length is deliberately not read.
        return new RawCellOmitted($"oversized text, over {allowedChars.ToString(CultureInfo.InvariantCulture)} chars; not loaded", preview, sb.Length);
    }

    static object ReadBinary(DbDataReader reader, int ordinal, int cellCap, ref long remaining, out bool omitted)
    {
        omitted = false;
        var allowed = (int)Math.Min(cellCap, Math.Max(0, remaining));
        var buffer = new byte[Math.Min(allowed + 1, 8192)];
        using var ms = new MemoryStream();
        long offset = 0;
        while (ms.Length <= allowed)
        {
            var want = (int)Math.Min(buffer.Length, allowed + 1 - ms.Length);
            var read = (int)reader.GetBytes(ordinal, offset, buffer, 0, want);
            if (read <= 0)
                break;
            ms.Write(buffer, 0, read);
            offset += read;
        }

        if (ms.Length <= allowed)
        {
            remaining -= ms.Length + FixedCellCharge;
            return ms.ToArray();
        }

        omitted = true;
        var head = ms.GetBuffer().AsSpan(0, (int)Math.Min(PreviewBytes, ms.Length));
        remaining -= allowed;
        return new RawCellOmitted(
            $"oversized binary, over {allowed.ToString(CultureInfo.InvariantCulture)} bytes; not loaded",
            "0x" + Convert.ToHexString(head), ms.Length);
    }
}
