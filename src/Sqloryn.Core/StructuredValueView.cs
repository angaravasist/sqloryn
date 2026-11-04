using System.Text;
using System.Text.Json;
using System.Xml;

namespace Sqloryn.Core;

/// <summary>Pretty-printed text for one retained cell, plus whether a formatted value is shown.</summary>
public readonly record struct StructuredValueViewResult(bool Shown, string Text);

/// <summary>
/// Read-only pretty printer for a retained xml cell or nvarchar JSON object/array.
/// It does not re-query and it does not execute the cell contents.
/// </summary>
public static class StructuredValueView
{
    public const string PolicyNote =
        "Shown from the retained cell. The content was not executed and the server was not queried again.";

    /// <summary>This viewer has no execution path. Callers must not send <see cref="StructuredValueViewResult.Text"/> to the server.</summary>
    public static bool ExecutesContent => false;

    public static bool CanView(string? sqlType, CellValue cell)
    {
        if (cell.IsNull || cell.Display.Length == 0)
            return false;
        if (IsXml(sqlType))
            return true;
        return IsNvarchar(sqlType) && LooksLikeJson(cell.Display);
    }

    public static StructuredValueViewResult Present(string? sqlType, CellValue cell)
    {
        if (!CanView(sqlType, cell))
            return new(false, "This cell is not an xml value or nvarchar JSON object or array. " + PolicyNote);

        try
        {
            var formatted = IsXml(sqlType) ? PrettyXml(cell.Display) : PrettyJson(cell.Display);
            var sb = new StringBuilder();
            if (cell.Truncated)
                sb.AppendLine("The retained cell was truncated. This view is incomplete and does not re-query the server.");
            sb.AppendLine(formatted.TrimEnd());
            sb.Append(PolicyNote);
            return new(true, sb.ToString());
        }
        catch (Exception ex) when (ex is XmlException or JsonException)
        {
            var why = IsXml(sqlType)
                ? "This retained value is not well-formed XML (" + SchemaObjectCatalog.OneLine(ex.Message) + ")."
                : "This retained nvarchar value looks like JSON but could not be read (" + SchemaObjectCatalog.OneLine(ex.Message) + ").";
            return new(false, why + " It was not executed. " + PolicyNote);
        }
    }

    static bool IsXml(string? sqlType)
        => sqlType is not null && sqlType.Trim().Equals("xml", StringComparison.OrdinalIgnoreCase);

    static bool IsNvarchar(string? sqlType)
    {
        if (string.IsNullOrWhiteSpace(sqlType))
            return false;
        var name = sqlType.Trim();
        return name.Equals("nvarchar", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("nvarchar(", StringComparison.OrdinalIgnoreCase);
    }

    static bool LooksLikeJson(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
                continue;
            return ch is '{' or '[';
        }
        return false;
    }

    static string PrettyXml(string xml)
    {
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ConformanceLevel = ConformanceLevel.Fragment,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CloseInput = true
        };
        var writerSettings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Document,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace
        };
        using var reader = XmlReader.Create(new StringReader(xml), readerSettings);
        var output = new StringWriter();
        using (var writer = XmlWriter.Create(output, writerSettings))
        {
            var document = new XmlDocument { XmlResolver = null };
            document.Load(reader);
            document.Save(writer);
        }
        return output.ToString();
    }

    static string PrettyJson(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 64
        });
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
}
