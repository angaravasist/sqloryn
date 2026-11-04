using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Sqloryn.Core;

/// <summary>
/// WS3-T1 SQL editor: go-to-line parsing, T-SQL highlighting definition, offline/responsiveness bounds and
/// App wiring source scans (line-number gutter, accessible name, selection offsets). Component-only: no SQL Server.
/// </summary>
static class EditorTests
{
    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"FAIL {name}");
    }

    static void Pass(string name) => Console.WriteLine($"PASS {name}");

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SQLoryn.slnx")))
                return d.FullName;
        throw new Exception("FAIL repo root (SQLoryn.slnx) not found from test base directory");
    }

    public static void Run()
    {
        GoToLineParsing();
        LineMath();
        Highlighting();
        LargeDocumentBound();
        AppWiring();
        DependencyRecord();
    }

    static void GoToLineParsing()
    {
        Check(GoToLine.TryParse("12", 100, out var t) && t == new GoToTarget(12, 1), "ws3-goto-plain-line");
        Check(GoToLine.TryParse("  7:15 ", 100, out t) && t == new GoToTarget(7, 15), "ws3-goto-line-and-column");
        Check(GoToLine.TryParse("100", 100, out t) && t.Line == 100, "ws3-goto-last-line-accepted");
        Check(!GoToLine.TryParse("101", 100, out _), "ws3-goto-past-end-rejected");
        Check(!GoToLine.TryParse("0", 100, out _), "ws3-goto-zero-rejected");
        Check(!GoToLine.TryParse("-3", 100, out _), "ws3-goto-negative-rejected");
        Check(!GoToLine.TryParse("", 100, out _) && !GoToLine.TryParse(null, 100, out _), "ws3-goto-empty-rejected");
        Check(!GoToLine.TryParse("abc", 100, out _) && !GoToLine.TryParse("1:x", 100, out _) && !GoToLine.TryParse("1:0", 100, out _), "ws3-goto-garbage-rejected");
        Check(!GoToLine.TryParse("99999999999999999999", 100, out _), "ws3-goto-overflow-rejected");
        Check(GoToLine.Describe("101", 100).Contains("100", StringComparison.Ordinal), "ws3-goto-error-names-range");
        Pass("ws3-go-to-line-parse");
    }

    static void LineMath()
    {
        Check(GoToLine.CountLines("") == 1 && GoToLine.CountLines("a") == 1, "ws3-lines-empty-and-single");
        Check(GoToLine.CountLines("a\nb\nc") == 3 && GoToLine.CountLines("a\r\nb\r\n") == 3 && GoToLine.CountLines("a\rb") == 2, "ws3-lines-mixed-endings");
        var text = "ab\r\ncd\nef";
        Check(GoToLine.OffsetOfLine(text, 1) == 0 && GoToLine.OffsetOfLine(text, 2) == 4 && GoToLine.OffsetOfLine(text, 3) == 7, "ws3-offset-of-line");
        Check(GoToLine.OffsetOfLine(text, 4) == -1 && GoToLine.OffsetOfLine(text, 0) == -1, "ws3-offset-out-of-range");
        // Round trip with the offsets used by selection execution: line/col of an offset is the inverse.
        for (var off = 0; off <= text.Length; off++)
        {
            var (line, col) = GoToLine.PositionOf(text, off);
            var back = GoToLine.OffsetOfLine(text, line) + (col - 1);
            Check(back == off || (off > 0 && text[off - 1] == '\r' && off < text.Length && text[off] == '\n'), $"ws3-position-roundtrip-{off}");
        }
        Pass("ws3-line-offset-math");
    }

    static void Highlighting()
    {
        Check(TsqlHighlighting.Keywords.Contains("SELECT") && TsqlHighlighting.Keywords.Contains("TRANSACTION") &&
              TsqlHighlighting.Keywords.Contains("OUTER") && TsqlHighlighting.Keywords.Count >= 80, "ws3-keywords-present");
        Check(TsqlHighlighting.Keywords.All(k => k.All(c => char.IsAsciiLetterUpper(c))), "ws3-keywords-plain-uppercase-words");
        foreach (var dark in new[] { true, false })
        {
            var xshd = TsqlHighlighting.BuildXshd(dark);
            var doc = XDocument.Parse(xshd); // well-formed
            var ns = doc.Root!.Name.Namespace;
            Check(doc.Root.Name.LocalName == "SyntaxDefinition" && (string?)doc.Root.Attribute("name") == "T-SQL", "ws3-xshd-root");
            var spans = doc.Descendants(ns + "Span").Select(s => (string?)s.Attribute("color")).ToList();
            Check(spans.Contains("Comment") && spans.Contains("String") && spans.Contains("QuotedIdentifier"), $"ws3-xshd-spans-{dark}");
            Check(xshd.Contains("--", StringComparison.Ordinal) && xshd.Contains("/\\*", StringComparison.Ordinal), $"ws3-xshd-comments-{dark}");
            var kw = doc.Descendants(ns + "Keywords").First(k => (string?)k.Attribute("color") == "Keyword");
            Check(kw.Elements(ns + "Word").Count() == TsqlHighlighting.Keywords.Count, $"ws3-xshd-all-keywords-{dark}");
            Check(doc.Descendants(ns + "Color").Any(c => (string?)c.Attribute("name") == "Number"), $"ws3-xshd-number-{dark}");
            // Every colour is a plain #RRGGBB literal (no external resources, no network references).
            Check(doc.Descendants().Attributes("foreground").All(a => System.Text.RegularExpressions.Regex.IsMatch(a.Value, "^#[0-9A-Fa-f]{6}$")), $"ws3-xshd-colours-literal-{dark}");
            Check(!doc.Descendants().SelectMany(e => e.Attributes()).Where(a => !a.IsNamespaceDeclaration).Any(a => a.Value.Contains("://", StringComparison.Ordinal)), $"ws3-xshd-no-external-{dark}");
        }
        Check(TsqlHighlighting.BuildXshd(true) != TsqlHighlighting.BuildXshd(false), "ws3-xshd-palettes-differ");
        Pass("ws3-tsql-highlighting-definition");
    }

    static void LargeDocumentBound()
    {
        // ~2 MB of T-SQL (about 45k lines). Pure line math must stay far below a keystroke budget; the real
        // editor gutter/highlighting are lazy and cannot be measured here, so the bound documents the Core part.
        var sb = new StringBuilder(2_200_000);
        while (sb.Length < 2_000_000)
            sb.Append("SELECT id, name FROM dbo.Orders WHERE id = 42 -- row\r\n");
        var text = sb.ToString();
        var sw = Stopwatch.StartNew();
        var lines = GoToLine.CountLines(text);
        var offset = GoToLine.OffsetOfLine(text, lines - 5);
        var pos = GoToLine.PositionOf(text, text.Length - 3);
        sw.Stop();
        Check(text.Length >= 2_000_000 && lines > 30_000 && offset > 0 && pos.Line == lines - 1 && GoToLine.OffsetOfLine(text, lines) == text.Length, "ws3-large-2mb-line-math-correct");
        Check(sw.ElapsedMilliseconds < 250, $"ws3-large-2mb-line-math-bounded ({sw.ElapsedMilliseconds} ms)");
        Pass("ws3-large-document-bound");
    }

    static void AppWiring()
    {
        var appDir = Path.Combine(FindRepoRoot(), "src", "Sqloryn.App");
        var doc = XDocument.Load(Path.Combine(appDir, "MainWindow.axaml"));
        var editor = doc.Descendants().FirstOrDefault(e => e.Attribute("Name")?.Value == "Editor");
        Check(editor is not null && editor.Name.LocalName == "TextEditor", "ws3-app-editor-is-texteditor");
        Check(editor!.Attribute("ShowLineNumbers")?.Value == "True", "ws3-app-line-number-gutter-present");
        Check(editor.Attribute("AutomationProperties.Name")?.Value == "SQL editor", "ws3-app-editor-automation-name-retained");
        Check(editor.Attribute("TextChanged") is not null || File.ReadAllText(Path.Combine(appDir, "MainWindow.axaml.cs")).Contains("Editor.TextChanged +=", StringComparison.Ordinal), "ws3-app-text-changed-wired");

        var main = File.ReadAllText(Path.Combine(appDir, "MainWindow.axaml.cs"));
        Check(main.Contains("Editor.SelectionStart", StringComparison.Ordinal) && main.Contains("_active.SelectionStart = start", StringComparison.Ordinal), "ws3-app-selection-offsets-captured");
        Check(main.Contains("Document.SetText(Editor.Text", StringComparison.Ordinal), "ws3-app-script-document-source-of-truth");

        var shell = File.ReadAllText(Path.Combine(appDir, "MainWindow.Shell.cs"));
        Check(shell.Contains("Go to _Line", StringComparison.Ordinal) && shell.Contains("Key.G", StringComparison.Ordinal), "ws3-app-go-to-line-command");
        Check(File.Exists(Path.Combine(appDir, "GoToLineWindow.axaml")) && File.Exists(Path.Combine(appDir, "GoToLineWindow.axaml.cs")), "ws3-app-go-to-line-dialog");
        Check(File.ReadAllText(Path.Combine(appDir, "FindReplaceWindow.axaml.cs")).Contains("TextEditor", StringComparison.Ordinal), "ws3-app-find-replace-targets-editor");

        var appXaml = File.ReadAllText(Path.Combine(appDir, "App.axaml"));
        Check(appXaml.Contains("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml", StringComparison.Ordinal), "ws3-app-avaloniaedit-theme-included");

        // Offline typing: the editor sources must not reference any networking API.
        foreach (var file in new[] { "MainWindow.axaml.cs", "MainWindow.Shell.cs", "GoToLineWindow.axaml.cs", "EditorSupport.cs" })
        {
            var path = Path.Combine(appDir, file);
            Check(File.Exists(path), $"ws3-app-file-{file}");
            var src = File.ReadAllText(path);
            Check(!src.Contains("HttpClient", StringComparison.Ordinal) && !src.Contains("System.Net.Http", StringComparison.Ordinal) &&
                  !src.Contains("WebRequest", StringComparison.Ordinal) && !src.Contains("Socket", StringComparison.Ordinal), $"ws3-app-no-network-{file}");
        }
        Pass("ws3-app-wiring-source-scan");
    }

    static void DependencyRecord()
    {
        var root = FindRepoRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "src", "Sqloryn.App", "Sqloryn.csproj"));
        Check(csproj.Contains("Avalonia.AvaloniaEdit", StringComparison.Ordinal), "ws3-dep-package-referenced");
        var lockFile = File.ReadAllText(Path.Combine(root, "src", "Sqloryn.App", "packages.lock.json"));
        Check(lockFile.Contains("\"Avalonia.AvaloniaEdit\"", StringComparison.Ordinal) && lockFile.Contains("contentHash", StringComparison.Ordinal), "ws3-dep-locked");
        var deps = File.ReadAllText(Path.Combine(root, "docs", "DEPENDENCIES.md"));
        Check(deps.Contains("Avalonia.AvaloniaEdit", StringComparison.Ordinal) && deps.Contains("MIT", StringComparison.Ordinal), "ws3-dep-inventory");
        Check(Directory.GetFiles(Path.Combine(root, "docs", "decisions"), "*editor*").Length == 1, "ws3-dep-adr-present");
        Pass("ws3-dependency-record");
    }
}
