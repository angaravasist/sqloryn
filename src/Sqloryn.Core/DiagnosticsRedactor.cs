using System.Text;
using System.Text.RegularExpressions;

namespace Sqloryn.Core;

public static partial class DiagnosticsRedactor
{
    public const string Placeholder = "***REDACTED***";

    [GeneratedRegex(
        """\b(password|pwd|access[ _]?token|accountkey|sharedaccesssignature)(\s*=\s*)("(?:[^"]|"")*"|N?'(?:[^']|'')*'|\{(?:[^}]|\}\})*\}|[^;\r\n]*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignment();

    [GeneratedRegex(@"\b(authorization\s*[:=]\s*)(?:(?:bearer|basic)\s+)?[^\s;,]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"\bbearer\s+[A-Za-z0-9\-._~+/]{8,}=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex JwtLike();

    [GeneratedRegex(@"\b(api[_\-]?key|client[_\-]?secret|secret|token)(\s*[:=]\s*)[^\s;,&]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericSecret();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        var result = AuthorizationHeader().Replace(text, m => m.Groups[1].Value + Placeholder);
        result = CredentialAssignment().Replace(result, m => m.Groups[1].Value + m.Groups[2].Value + Placeholder);
        result = BearerToken().Replace(result, "Bearer " + Placeholder);
        result = JwtLike().Replace(result, Placeholder);
        result = GenericSecret().Replace(result, m => m.Groups[1].Value + m.Groups[2].Value + Placeholder);
        return result;
    }

    public static string BuildSupportBundle(IEnumerable<(string Title, string Text)> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var sb = new StringBuilder();
        sb.Append("SQLoryn support bundle (redacted)\n");
        foreach (var (title, text) in parts)
        {
            sb.Append("\n=== ").Append(Redact(title).Replace('\n', ' ').Replace('\r', ' ')).Append(" ===\n");
            sb.Append(Redact(text));
            if (!(text?.EndsWith('\n') ?? true))
                sb.Append('\n');
        }
        return sb.ToString();
    }
}
