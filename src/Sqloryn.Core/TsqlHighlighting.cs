using System.Security;
using System.Text;

namespace Sqloryn.Core;

/// <summary>
/// Minimal T-SQL syntax-highlighting definition (AvalonEdit "xshd" format) generated from a keyword list.
/// Pure data: no I/O, no network, no SQL Server metadata. Colours are literal #RRGGBB so the definition has no
/// external resources. This is lexical colouring only; it does not parse or validate SQL.
/// </summary>
public static class TsqlHighlighting
{
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUTHORIZATION", "BACKUP", "BEGIN", "BETWEEN", "BREAK",
        "BY", "CASCADE", "CASE", "CATCH", "CHECK", "CLOSE", "CLUSTERED", "COLUMN", "COMMIT", "CONSTRAINT",
        "CONTINUE", "CREATE", "CROSS", "CURSOR", "DATABASE", "DEALLOCATE", "DECLARE", "DEFAULT", "DELETE", "DESC",
        "DISTINCT", "DROP", "ELSE", "END", "ESCAPE", "EXCEPT", "EXEC", "EXECUTE", "EXISTS", "FETCH", "FOR",
        "FOREIGN", "FROM", "FULL", "FUNCTION", "GOTO", "GRANT", "GROUP", "HAVING", "IDENTITY", "IF", "IN", "INDEX",
        "INNER", "INSERT", "INTERSECT", "INTO", "IS", "JOIN", "KEY", "LEFT", "LIKE", "MERGE", "NOT", "NULL",
        "NONCLUSTERED", "OFFSET", "ON", "OPEN", "OPTION", "OR", "ORDER", "OUTER", "OUTPUT", "OVER", "PRIMARY",
        "PRINT", "PROC", "PROCEDURE", "REFERENCES", "RETURN", "REVOKE", "RIGHT", "ROLLBACK", "ROWS", "SCHEMA",
        "SELECT", "SET", "TABLE", "THEN", "THROW", "TOP", "TRAN", "TRANSACTION", "TRIGGER", "TRUNCATE", "TRY",
        "UNION", "UNIQUE", "UPDATE", "USE", "VALUES", "VIEW", "WAIT", "WHEN", "WHERE", "WHILE", "WITH"
    };

    /// <summary>Builds the xshd text for the dark or light palette (foreground colours only; the editor owns the background).</summary>
    public static string BuildXshd(bool dark)
    {
        var p = dark
            ? (Keyword: "#569CD6", String: "#CE9178", Comment: "#6A9955", Number: "#B5CEA8", Quoted: "#4EC9B0", Var: "#9CDCFE")
            : (Keyword: "#0000FF", String: "#A31515", Comment: "#007A00", Number: "#098658", Quoted: "#267F99", Var: "#001080");
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>\n");
        sb.Append("<SyntaxDefinition name=\"T-SQL\" xmlns=\"http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008\">\n");
        sb.Append($"  <Color name=\"Keyword\" foreground=\"{p.Keyword}\" fontWeight=\"bold\" />\n");
        sb.Append($"  <Color name=\"String\" foreground=\"{p.String}\" />\n");
        sb.Append($"  <Color name=\"Comment\" foreground=\"{p.Comment}\" />\n");
        sb.Append($"  <Color name=\"Number\" foreground=\"{p.Number}\" />\n");
        sb.Append($"  <Color name=\"QuotedIdentifier\" foreground=\"{p.Quoted}\" />\n");
        sb.Append($"  <Color name=\"Variable\" foreground=\"{p.Var}\" />\n");
        sb.Append("  <RuleSet ignoreCase=\"true\">\n");
        // Spans first: comments, strings, and [bracket]/"quoted" identifiers.
        sb.Append("    <Span color=\"Comment\" begin=\"--\" />\n");
        sb.Append("    <Span color=\"Comment\" multiline=\"true\" begin=\"/\\*\" end=\"\\*/\" />\n");
        // A doubled '' inside a string simply closes and reopens the span, which renders identically.
        sb.Append("    <Span color=\"String\" multiline=\"true\" begin=\"N?'\" end=\"'\" />\n");
        sb.Append("    <Span color=\"QuotedIdentifier\" begin=\"\\[\" end=\"\\]\" />\n");
        sb.Append("    <Span color=\"QuotedIdentifier\" begin=\"&quot;\" end=\"&quot;\" />\n");
        sb.Append("    <Keywords color=\"Keyword\">\n");
        foreach (var word in Keywords.OrderBy(k => k, StringComparer.Ordinal))
            sb.Append("      <Word>").Append(SecurityElement.Escape(word)).Append("</Word>\n");
        sb.Append("    </Keywords>\n");
        sb.Append("    <Rule color=\"Variable\">@@?[A-Za-z_][A-Za-z0-9_]*</Rule>\n");
        sb.Append("    <Rule color=\"Number\">\\b0[xX][0-9A-Fa-f]+\\b|\\b\\d+(\\.\\d+)?([eE][+-]?\\d+)?\\b</Rule>\n");
        sb.Append("  </RuleSet>\n");
        sb.Append("</SyntaxDefinition>\n");
        return sb.ToString();
    }
}
