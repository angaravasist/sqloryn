# ADR-005: AvaloniaEdit for the SQL editor

Date: 2026-11-04. Extends [ADR-003](003-desktop-avalonia.md).

## Decision

Use [`Avalonia.AvaloniaEdit`](https://www.nuget.org/packages/Avalonia.AvaloniaEdit) (`AvaloniaEdit.TextEditor`) instead of a multiline `TextBox`.

| Check | Result |
|---|---|
| Licence | MIT (Avalonia Team; port of ICSharpCode AvalonEdit). Keep the upstream copyright notice in distributed builds. |
| Compatibility | Requires Avalonia 12 on `net10.0`. Adds no transitive packages. |
| Content | Managed DLL only, with no native assets or network code. Its built-in highlighter definitions are unused. |

## How it is used

- Line numbers, scrolling, undo/redo and standard keyboard editing come from the component.
- T-SQL colouring covers keywords, comments, strings, bracketed/quoted identifiers, `@variables` and numbers. The definition comes from `Sqloryn.Core.TsqlHighlighting`, with separate dark and light palettes. It does lexical colouring only and does not validate SQL.
- Go to Line (Ctrl+G) accepts `line` or `line:column`, parsed by `Sqloryn.Core.GoToLine`.
- `ScriptDocument` stays the source of truth. The editor text is copied into it on every change, tab switch, save, run and replace. Selection execution uses the editor's selection offsets.
- Typing is local only: no completion service and no network calls.

## Rejected alternatives

| Option | Reason |
|---|---|
| `TextBox` plus a hand-built line-number column | Needs scroll synchronisation, has no highlighting, and every later editor feature would be custom. |
| `AvaloniaEdit` 11.x | Targets Avalonia 11; mixing major versions is unsupported. |
| Monaco in a WebView | Adds a WebView, a JavaScript bridge and a larger security surface. |
| Tree-sitter / Scintilla | Native binaries per architecture and no first-class Avalonia host. |
| Microsoft SQL parsers (ScriptDom/SMO) for colouring | Need a separate licence review; lexical colouring does not need a parser. |
