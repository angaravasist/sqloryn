using System.Globalization;
using System.Text;
using System.Xml;

namespace Sqloryn.Core;

/// <summary>One <c>RelOp</c> from a SHOWPLAN document. All text is untrusted server output, already single-line and bounded.</summary>
/// <param name="Depth">Nesting depth of the operator in the plan tree; the root operator is 0.</param>
/// <param name="EstimatedSubtreeCost"><c>EstimatedTotalSubtreeCost</c> when present and finite; null otherwise (never defaulted to 0).</param>
public sealed record PlanOperator(int Depth, int? NodeId, string PhysicalOp, string LogicalOp, double? EstimatedSubtreeCost);

/// <param name="Parsed">False when the document was rejected (malformed, DTD present, over a limit). Nothing is partially trusted then.</param>
/// <param name="Truncated">More than <see cref="SafeShowPlanXml.MaxOperators"/> operators exist; the list is a prefix.</param>
public sealed record ShowPlanSummary(bool Parsed, IReadOnlyList<PlanOperator> Operators, bool Truncated, string? Error);

/// <summary>
/// Reads the operator list from SHOWPLAN XML for display. The plan text comes from a server (or a pasted file), so it is
/// untrusted: DTDs and entities are prohibited outright, no resolver is installed (nothing is ever fetched from disk or the
/// network), and the document size is capped. This type only reads text; it never runs SQL.
/// </summary>
public static class SafeShowPlanXml
{
    public const string ShowPlanNamespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    public const int MaxOperators = 500;
    const int MaxNameChars = 80;

    public static ShowPlanSummary Parse(string? planXml)
    {
        if (string.IsNullOrWhiteSpace(planXml))
            return Rejected("The plan XML is empty.");
        if (planXml.Length > EstimatedPlanService.MaxPlanCharacters)
            return Rejected("The plan XML is too large to summarise.");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = EstimatedPlanService.MaxPlanCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true
        };

        var operators = new List<PlanOperator>();
        var truncated = false;
        try
        {
            using var reader = XmlReader.Create(new StringReader(planXml), settings);
            var open = new Stack<bool>(); // one entry per open element: is it a RelOp?
            var relDepth = 0;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    var isRel = reader.LocalName == "RelOp" && reader.NamespaceURI == ShowPlanNamespace;
                    if (isRel)
                    {
                        if (operators.Count < MaxOperators)
                            operators.Add(new PlanOperator(
                                relDepth,
                                ParseInt(reader.GetAttribute("NodeId")),
                                Clean(reader.GetAttribute("PhysicalOp")),
                                Clean(reader.GetAttribute("LogicalOp")),
                                ParseCost(reader.GetAttribute("EstimatedTotalSubtreeCost"))));
                        else
                            truncated = true;
                    }
                    if (!reader.IsEmptyElement)
                    {
                        open.Push(isRel);
                        if (isRel)
                            relDepth++;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && open.Count > 0 && open.Pop())
                {
                    relDepth--;
                }
            }
        }
        catch (XmlException ex)
        {
            // Fixed text only: the exception message can echo document content.
            return Rejected($"The plan XML was rejected: it is malformed or uses a DTD/entity, which is not allowed (line {ex.LineNumber}).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            return Rejected("The plan XML could not be read safely.");
        }

        return new ShowPlanSummary(true, operators, truncated, null);
    }

    static ShowPlanSummary Rejected(string message) => new(false, [], false, message);

    static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    static double? ParseCost(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) && d >= 0 ? d : null;

    static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var sb = new StringBuilder(Math.Min(value.Length, MaxNameChars + 1));
        foreach (var ch in value)
        {
            if (sb.Length >= MaxNameChars)
            {
                sb.Append('…');
                break;
            }
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return sb.ToString();
    }

    /// <summary>Indented one-line-per-operator text for the Messages pane. Costs are estimates, not measurements.</summary>
    public static string FormatSummary(ShowPlanSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!summary.Parsed)
            return "Operator summary unavailable: " + (summary.Error ?? "the plan could not be read.") + "\n";
        var sb = new StringBuilder();
        sb.Append("Operators (").Append(summary.Operators.Count).Append(summary.Truncated ? "+" : "").Append("), estimated subtree cost:\n");
        if (summary.Operators.Count == 0)
            sb.Append("  (no operators found in this plan)\n");
        foreach (var op in summary.Operators)
        {
            sb.Append("  ").Append(' ', Math.Min(op.Depth, 24) * 2).Append(op.PhysicalOp);
            if (op.LogicalOp.Length > 0 && !op.LogicalOp.Equals(op.PhysicalOp, StringComparison.Ordinal))
                sb.Append(" (").Append(op.LogicalOp).Append(')');
            if (op.EstimatedSubtreeCost is { } cost)
                sb.Append(" · subtree cost ").Append(cost.ToString("0.######", CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        if (summary.Truncated)
            sb.Append("  … more operators exist; the full XML below is complete.\n");
        return sb.ToString();
    }
}
