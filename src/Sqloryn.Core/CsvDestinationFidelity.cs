using System.Globalization;

namespace Sqloryn.Core;

/// <summary>
/// Exact destination-type fidelity checks for Append/Replace. Successful SqlClient conversion is not enough:
/// values that would round, truncate or change encoding are refused before any DELETE/INSERT.
/// </summary>
public static class CsvDestinationFidelity
{
    static readonly DateTime DatetimeMin = new(1753, 1, 1);
    static readonly DateTime DatetimeMax = new(9999, 12, 31, 23, 59, 59, 997);
    static readonly DateTime SmallDatetimeMin = new(1900, 1, 1);
    static readonly DateTime SmallDatetimeMax = new(2079, 6, 6, 23, 59, 0);
    static readonly decimal MoneyMin = -922337203685477.5808m;
    static readonly decimal MoneyMax = 922337203685477.5807m;
    static readonly decimal SmallMoneyMin = -214748.3648m;
    static readonly decimal SmallMoneyMax = 214748.3647m;

    public static bool TryAccept(string cell, CsvDestinationType destination, bool emptyTextAsNull, out string? error)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(destination);
        error = null;
        var name = (destination.SqlTypeName ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0)
        {
            error = "has no destination type metadata";
            return false;
        }

        var text = name is "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext"
            ? cell
            : cell.Trim();

        if (text.Length == 0)
        {
            if (name is "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext" or "xml")
            {
                if (emptyTextAsNull)
                    return true;
                return CheckTextLength("", destination, name, out error);
            }
            return true; // NULL for non-text
        }

        return name switch
        {
            "decimal" or "numeric" => CheckDecimal(text, destination.Precision ?? 18, destination.Scale ?? 0, out error),
            "money" => CheckMoney(text, MoneyMin, MoneyMax, out error),
            "smallmoney" => CheckMoney(text, SmallMoneyMin, SmallMoneyMax, out error),
            "float" or "real" => CheckFloat(text, out error),
            "tinyint" => CheckIntRange(text, 0, 255, out error),
            "smallint" => CheckIntRange(text, short.MinValue, short.MaxValue, out error),
            "int" => CheckIntRange(text, int.MinValue, int.MaxValue, out error),
            "bigint" => CheckBigInt(text, out error),
            "bit" => CsvValue.TryParseBit(text, out _) || text is "0" or "1"
                ? true
                : Fail("is not true, false, 1 or 0", out error),
            "varchar" or "char" or "text" => CheckVarchar(text, destination, name, out error),
            "nvarchar" or "nchar" or "ntext" => CheckTextLength(text, destination, name, out error),
            "date" => CheckDate(text, DateTime.MinValue.Date, DateTime.MaxValue.Date, requireTime: false, maxFraction: 0, out error),
            "datetime" => CheckDate(text, DatetimeMin, DatetimeMax, requireTime: true, maxFraction: 3, out error),
            "smalldatetime" => CheckSmallDatetime(text, out error),
            "datetime2" => CheckDate(text, DateTime.MinValue, DateTime.MaxValue, requireTime: true, maxFraction: destination.Scale ?? 7, out error),
            "time" => CheckTime(text, destination.Scale ?? 7, out error),
            "uniqueidentifier" => CsvValue.TryParseGuid(text, out _)
                ? true
                : Fail("is not a GUID", out error),
            _ => true // unknown types: rely on parse type + server; do not invent rules
        };
    }

    static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    static bool CheckDecimal(string text, int precision, int scale, out string? error)
    {
        error = null;
        if (precision is < 1 or > 38 || scale is < 0 or > 38 || scale > precision)
            return Fail($"targets an invalid decimal({precision},{scale})", out error);
        if (!CsvValue.TryParseDecimalText(text, strictLeadingZeros: false, out var integerDigits, out var frac))
            return Fail("is not a plain decimal number (use '.' for the decimal point; no thousands separators or exponent)", out error);
        if (frac > scale)
            return Fail($"has {frac} fractional digit(s) but the destination decimal({precision},{scale}) scale only stores {scale}", out error);
        var maxInteger = precision - scale;
        if (integerDigits > maxInteger)
            return Fail($"has {integerDigits} digit(s) before the decimal point but decimal({precision},{scale}) allows at most {maxInteger}", out error);
        return true;
    }

    static bool CheckMoney(string text, decimal min, decimal max, out string? error)
    {
        error = null;
        if (!CsvValue.TryParseDecimalText(text, strictLeadingZeros: false, out _, out var frac))
            return Fail("is not a plain decimal number for money", out error);
        if (frac > 4)
            return Fail($"has {frac} fractional digit(s) but money only stores 4 without rounding", out error);
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return Fail("is not a money value", out error);
        if (value < min || value > max)
            return Fail($"is outside the money range ({min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)})", out error);
        return true;
    }

    static bool CheckFloat(string text, out string? error)
    {
        if (CsvValue.TryParseDecimalText(text, strictLeadingZeros: false, out _, out _))
        {
            error = null;
            return true;
        }
        return Fail("is not a plain number", out error);
    }

    static bool CheckIntRange(string text, long min, long max, out string? error)
    {
        if (!CsvValue.TryParseInt64(text, strictLeadingZeros: false, out var value) || value < min || value > max)
            return Fail($"is not a whole number in range {min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)}", out error);
        error = null;
        return true;
    }

    static bool CheckBigInt(string text, out string? error)
    {
        if (!CsvValue.TryParseInt64(text, strictLeadingZeros: false, out _))
            return Fail("is not a whole number that fits a 64-bit bigint", out error);
        error = null;
        return true;
    }

    static bool CheckVarchar(string text, CsvDestinationType destination, string name, out string? error)
    {
        if (!CheckTextLength(text, destination, name, out error))
            return false;
        // varchar uses a single-byte code page; refuse non-ASCII so the server never substitutes '?' / best-fit.
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] > 0x7F)
                return Fail("contains non-ASCII characters that cannot be stored faithfully in varchar (use nvarchar for Unicode)", out error);
        }
        return true;
    }

    static bool CheckTextLength(string text, CsvDestinationType destination, string name, out string? error)
    {
        error = null;
        var max = destination.MaxLength;
        if (max is null or < 0)
            return true; // MAX / unknown
        // Catalog MaxLength is already in characters for nchar/nvarchar (see MetadataCatalog.ListColumns).
        var charLimit = max.Value;
        if (charLimit <= 0)
            return true;
        if (text.Length > charLimit)
            return Fail($"is {text.Length} character(s) but the destination {name}({charLimit.ToString(CultureInfo.InvariantCulture)}) only holds {charLimit}", out error);
        return true;
    }

    static bool CheckDate(string text, DateTime min, DateTime max, bool requireTime, int maxFraction, out string? error)
    {
        error = null;
        if (!CsvValue.TryParseDateTimeText(text, out var normalized))
            return Fail("is not an ISO 8601 date or datetime", out error);
        if (!DateTime.TryParseExact(normalized, CsvValue.DateTimeWireFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return Fail("is not a valid datetime", out error);
        if (value < min || value > max)
            return Fail($"is outside the destination type range ({min:yyyy-MM-dd} .. {max:yyyy-MM-dd})", out error);
        var dot = text.IndexOf('.');
        if (dot >= 0)
        {
            var frac = 0;
            for (var i = dot + 1; i < text.Length && text[i] is >= '0' and <= '9'; i++)
                frac++;
            if (frac > maxFraction)
                return Fail($"has {frac} fractional second digit(s) but the destination only stores {maxFraction}", out error);
        }
        _ = requireTime;
        return true;
    }

    static bool CheckSmallDatetime(string text, out string? error)
    {
        if (!CheckDate(text, SmallDatetimeMin, SmallDatetimeMax, requireTime: true, maxFraction: 0, out error))
            return false;
        if (!CsvValue.TryParseDateTimeText(text, out var normalized))
            return Fail("is not an ISO 8601 date or datetime", out error);
        if (!DateTime.TryParseExact(normalized, CsvValue.DateTimeWireFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return Fail("is not a valid datetime", out error);
        if (value.Second != 0 || value.Millisecond != 0)
            return Fail("has seconds or fractions that smalldatetime would round", out error);
        return true;
    }

    static bool CheckTime(string text, int scale, out string? error)
    {
        error = null;
        // Accept ISO datetime (date ignored) or HH:mm[:ss[.f]]
        if (CsvValue.TryParseDateTimeText(text, out var normalized))
        {
            if (!DateTime.TryParseExact(normalized, CsvValue.DateTimeWireFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return Fail("is not a valid time", out error);
            var dot = text.IndexOf('.');
            if (dot >= 0)
            {
                var frac = 0;
                for (var i = dot + 1; i < text.Length && text[i] is >= '0' and <= '9'; i++)
                    frac++;
                if (frac > scale)
                    return Fail($"has {frac} fractional second digit(s) but time({scale}) only stores {scale}", out error);
            }
            return true;
        }

        var formats = new[] { "HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF" };
        if (!TimeSpan.TryParseExact(text, formats, CultureInfo.InvariantCulture, out _))
            return Fail("is not a time value (HH:mm[:ss[.fffffff]])", out error);
        var tDot = text.IndexOf('.');
        if (tDot >= 0)
        {
            var frac = 0;
            for (var i = tDot + 1; i < text.Length && text[i] is >= '0' and <= '9'; i++)
                frac++;
            if (frac > scale)
                return Fail($"has {frac} fractional second digit(s) but time({scale}) only stores {scale}", out error);
        }
        return true;
    }
}
