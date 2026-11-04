# WS3-T1 editor: highlighting and large-document measurements

Component-level, 2026-11-04, macOS arm64, .NET 10.0.403, `Avalonia.AvaloniaEdit` 12.0.0. No SQL Server involved and no
GUI session: this exercises the AvaloniaEdit document/highlighter engine headlessly with the
`TsqlHighlighting.BuildXshd` output, not rendered pixels, VoiceOver or key handling. A throwaway console project
(not committed) loaded both palettes through `HighlightingLoader` and highlighted
`SELECT 1, 'it''s' /* c⏎ x */ FROM [dbo].[T] WHERE @x = 0x1F -- end⏎USE db`.

| Check | Result |
|---|---|
| Both xshd palettes load | PASS (13 sections each) |
| Colour classes seen | keyword, number, string (`'it''s'`), multi-line block comment across two lines, `[bracket]` identifier, `@variable`, hex number, `--` comment |
| 2 MB document (37,039 lines) load into `TextDocument` | 30 ms |
| First-line highlight / 50 mid-document lines with lazy state catch-up | 0 ms / 49 ms |
| Insert one character + `Text` getter (what each keystroke's sync costs) | 3 ms |

Limitations: single run on one machine; the visual control's layout/render time, scrolling, IME and VoiceOver were not
measured. `ScriptDocument` sync copies the whole text per change (as the previous TextBox did); the 3 ms above bounds
that copy at 2 MB. Re-measure in the packaged app before any performance claim.
