using System.Globalization;

namespace Sqloryn.Core;

/// <summary>Column types the flat-file importer understands. Everything else is carried as text and left to the server's conversion.</summary>
public enum CsvSqlType
{
    NVarChar,
    Int,
    BigInt,
    Decimal,
    Bit,
    DateTime2,
    UniqueIdentifier
}

public static class CsvSqlTypes
{
    /// <summary>Maps an existing SQL Server type name (sys.types) to the import type used to parse and send values.</summary>
    public static CsvSqlType FromSqlTypeName(string typeName)
    {
        return (typeName ?? "").Trim().ToLowerInvariant() switch
        {
            "tinyint" or "smallint" or "int" => CsvSqlType.Int,
            "bigint" => CsvSqlType.BigInt,
            "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => CsvSqlType.Decimal,
            "bit" => CsvSqlType.Bit,
            "date" or "datetime" or "datetime2" or "smalldatetime" => CsvSqlType.DateTime2,
            "uniqueidentifier" => CsvSqlType.UniqueIdentifier,
            _ => CsvSqlType.NVarChar
        };
    }

    /// <summary>
    /// T-SQL type text for CREATE TABLE. <paramref name="length"/> is for text (null = MAX);
    /// <paramref name="scale"/> for decimals must be 0–38 (never silently clamped).
    /// </summary>
    public static string ToSqlTypeText(CsvSqlType type, int? length, int? scale) => type switch
    {
        CsvSqlType.Int => "int",
        CsvSqlType.BigInt => "bigint",
        CsvSqlType.Decimal => FormatDecimal(scale),
        CsvSqlType.Bit => "bit",
        CsvSqlType.DateTime2 => "datetime2(7)",
        CsvSqlType.UniqueIdentifier => "uniqueidentifier",
        _ => length is > 0 and <= 4000
            ? $"nvarchar({length.Value.ToString(CultureInfo.InvariantCulture)})"
            : "nvarchar(max)"
    };

    static string FormatDecimal(int? scale)
    {
        var s = scale ?? 10;
        if (s is < 0 or > 38)
            throw new ArgumentOutOfRangeException(nameof(scale), s, "Decimal scale must be between 0 and 38 inclusive.");
        return $"decimal(38, {s.ToString(CultureInfo.InvariantCulture)})";
    }
}

/// <summary>
/// Culture-independent parsing of flat-file cells. Numbers use '.' only, dates must be ISO 8601 (no day/month guessing),
/// so a value is never silently reinterpreted under the Mac's regional settings.
/// </summary>
static class CsvValue
{
    static readonly string[] DateFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF"
    ];

    public const string DateTimeWireFormat = "yyyy-MM-ddTHH:mm:ss.fffffff";

    /// <summary>Plain decimal text: optional sign, digits, optional fraction. No exponent, grouping or locale separators.</summary>
    public static bool TryParseDecimalText(string text, bool strictLeadingZeros, out int integerDigits, out int scale)
    {
        integerDigits = 0;
        scale = 0;
        var i = 0;
        if (text.Length > 0 && text[0] is '+' or '-')
            i = 1;
        var intStart = i;
        while (i < text.Length && text[i] is >= '0' and <= '9')
            i++;
        integerDigits = i - intStart;
        var hasFraction = false;
        if (i < text.Length && text[i] == '.')
        {
            i++;
            var fractionStart = i;
            while (i < text.Length && text[i] is >= '0' and <= '9')
                i++;
            scale = i - fractionStart;
            hasFraction = scale > 0;
        }
        if (i != text.Length || (integerDigits == 0 && !hasFraction))
            return false;
        if (integerDigits + scale > 38)
            return false;
        // "02134" is an identifier-like string, not the number 2134.
        if (strictLeadingZeros && integerDigits > 1 && text[intStart] == '0')
            return false;
        return true;
    }

    public static bool TryParseInt32(string text, bool strictLeadingZeros, out int value)
    {
        value = 0;
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
            return false;
        return !strictLeadingZeros || !HasLeadingZero(text);
    }

    public static bool TryParseInt64(string text, bool strictLeadingZeros, out long value)
    {
        value = 0;
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
            return false;
        return !strictLeadingZeros || !HasLeadingZero(text);
    }

    static bool HasLeadingZero(string text)
    {
        var start = text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
        return text.Length - start > 1 && text[start] == '0';
    }

    public static bool TryParseBit(string text, out bool value)
    {
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
        value = false;
        return false;
    }

    public static bool TryParseDateTimeText(string text, out string normalized)
    {
        normalized = "";
        if (!DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return false;
        normalized = value.ToString(DateTimeWireFormat, CultureInfo.InvariantCulture);
        return true;
    }

    public static bool TryParseGuid(string text, out Guid value)
        => Guid.TryParseExact(text, "D", out value);

    /// <summary>True when a spreadsheet would treat the cell as a formula (leading = + - @ tab or CR) and it is not just a signed number.</summary>
    public static bool IsFormulaLike(string cell)
    {
        if (cell.Length == 0)
            return false;
        var first = cell[0];
        if (first is not ('=' or '+' or '-' or '@' or '\t' or '\r'))
            return false;
        if (first is '+' or '-' && TryParseDecimalText(cell, strictLeadingZeros: false, out _, out _))
            return false;
        return true;
    }

    /// <summary>
    /// Converts one cell for sending as a parameter. Decimals and datetimes travel as validated invariant text so no
    /// provider or server setting can change their meaning; the generated SQL converts datetimes explicitly.
    /// </summary>
    public static bool TryConvert(string cell, CsvSqlType type, bool emptyTextAsNull, out object? value, out string? error)
    {
        value = null;
        error = null;
        ArgumentNullException.ThrowIfNull(cell);

        if (type == CsvSqlType.NVarChar)
        {
            value = cell.Length == 0 && emptyTextAsNull ? null : cell;
            return true;
        }

        var text = cell.Trim();
        if (text.Length == 0)
            return true;

        switch (type)
        {
            case CsvSqlType.Int:
                if (TryParseInt32(text, strictLeadingZeros: false, out var i)) { value = i; return true; }
                error = "is not a whole number that fits a 32-bit int";
                return false;
            case CsvSqlType.BigInt:
                if (TryParseInt64(text, strictLeadingZeros: false, out var l)) { value = l; return true; }
                error = "is not a whole number that fits a 64-bit bigint";
                return false;
            case CsvSqlType.Decimal:
                if (TryParseDecimalText(text, strictLeadingZeros: false, out _, out _)) { value = text; return true; }
                error = "is not a plain decimal number (use '.' for the decimal point; no thousands separators or exponent)";
                return false;
            case CsvSqlType.Bit:
                if (TryParseBit(text, out var b)) { value = b; return true; }
                if (text == "1") { value = true; return true; }
                if (text == "0") { value = false; return true; }
                error = "is not true, false, 1 or 0";
                return false;
            case CsvSqlType.DateTime2:
                if (TryParseDateTimeText(text, out var normalized)) { value = normalized; return true; }
                error = "is not an ISO 8601 date or datetime (yyyy-MM-dd[ HH:mm[:ss[.fffffff]]]); ambiguous formats are never guessed";
                return false;
            case CsvSqlType.UniqueIdentifier:
                if (TryParseGuid(text, out var g)) { value = g; return true; }
                error = "is not a GUID";
                return false;
            default:
                error = "has an unsupported type";
                return false;
        }
    }
}
