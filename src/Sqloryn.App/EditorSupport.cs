using System.Xml;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>
/// Editor-surface helpers: lexical T-SQL colouring and caret jumps. Everything here is local and synchronous;
/// typing never triggers I/O. The ScriptDocument remains the source of truth and is synced by MainWindow.
/// </summary>
static class EditorSupport
{
    static IHighlightingDefinition? _dark;
    static IHighlightingDefinition? _light;

    /// <summary>Highlighting for the dark or light palette (built once from <see cref="TsqlHighlighting"/>).</summary>
    public static IHighlightingDefinition Highlighting(bool dark)
    {
        if (dark)
            return _dark ??= Load(true);
        return _light ??= Load(false);
    }

    static IHighlightingDefinition Load(bool dark)
    {
        using var text = new StringReader(TsqlHighlighting.BuildXshd(dark));
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    /// <summary>Moves the caret to the (clamped) target and scrolls it into view. Selection is cleared.</summary>
    public static void GoTo(TextEditor editor, GoToTarget target)
    {
        var document = editor.Document;
        if (document is null || document.LineCount < 1)
            return;
        var line = document.GetLineByNumber(Math.Clamp(target.Line, 1, document.LineCount));
        var column = Math.Clamp(target.Column, 1, line.Length + 1);
        editor.CaretOffset = line.Offset + column - 1;
        editor.ScrollToLine(line.LineNumber);
    }
}
