# App-level journey checklist (offline)

Owner: SQLoryn maintainer. Reviewed: 2026-11-04. Experimental; no release support.

Offline checks that need no SQL Server. Per-journey status for the eight PR 7 journeys is in
[ssms/JOURNEY_EVIDENCE.md](ssms/JOURNEY_EVIDENCE.md). Nothing here is live SQL evidence, and nothing here shows
native UI behaviour that no one has observed.

Legend: **Pass (Core)** = automated assertion in `tests/Sqloryn.Core.Tests` (component level, fakes/no UI);
**Manual** = a person must walk it on a Mac and record the result; **Skip** = not run, with reason.

## 1. Keyboard and automation targets

Contract: each target has a stable `AutomationProperties.Name` and a shortcut. The Core test
`a11y-keyboard-contract-source-scan` reads the `.axaml`/`.cs` source and fails if a name, the dialog default button
or a shortcut registration disappears. It does not run Avalonia, so it cannot prove that VoiceOver speaks the name or
that the key reaches the control.

| Target | Control (`Name`) | `AutomationProperties.Name` | Keyboard | Source-scan | Native walk |
|---|---|---|---|---|---|
| Welcome: connect | `WelcomeConnect` | New connection | Tab to button, Space/Return; ⇧⌘C from the menu | Pass (Core) | Manual — not done |
| Welcome: new query | `WelcomeNewQuery` | New query | Tab, Space/Return; ⌘N | Pass (Core) | Manual — not done |
| Welcome: open file | `WelcomeOpen` | Open SQL file | Tab, Space/Return; ⌘O | Pass (Core) | Manual — not done |
| Toolbar: connect | `ConnectButton` | Connect | ⇧⌘C | Pass (Core) | Manual — not done |
| Toolbar: run | `RunButton` | Run query | F5 (all platforms); ⌘↵ on Mac, Ctrl+Enter elsewhere | Pass (Core): F5 handler and ⌘↵ registration present | Manual — not done |
| Toolbar: cancel | `CancelButton` | Cancel query | Esc while a command runs; ⌘. on Mac | Pass (Core): Esc handler present | Manual — not done |
| Sidebar toggle | `SidebarToggle` | Toggle object explorer sidebar | ⌃⌘S on Mac, Ctrl+B elsewhere | Pass (Core) | Manual — not done |
| Connect dialog: Connect / Cancel | unnamed buttons | Connect / Cancel connect | Return is the default button (`IsDefault`) | Pass (Core) | Manual — not done |

Manual walk (record Pass/Fail and the macOS version in `ssms/JOURNEY_EVIDENCE.md` Journey 1):

1. Launch with keyboard only. Reach all three welcome buttons with Tab, activate each, and confirm focus returns to
   a sensible control after the dialog closes.
2. With VoiceOver on, confirm the listed names are spoken, and that the system menu title reads "SQLoryn".
3. Press F5 and ⌘↵ with a script open; press Esc and ⌘. while a command runs.
4. Toggle the sidebar with its shortcut and with the toolbar button.

## 2. Document save conflicts (PR 7 finding 5)

- **Core:** `ScriptDocument.SaveAsync` refuses when the file changed on disk since open/last save and throws
  `ExternalDocumentChangedException` before the replace. Evidence: `d2-external-edit-survives-save` (external edit,
  external delete, force save, save-as, own-save tracking), `save-io-failure` (buffer stays dirty), and
  `save-concurrent-edit` (an edit during save keeps the document dirty).
- **App path:** `MainWindow.axaml.cs` catches `ExternalDocumentChangedException` *before* the generic handler. It
  writes nothing, keeps the buffer dirty, and shows a message offering Save As. A generic failure shows
  `Save failed: …` and also keeps the buffer dirty. `a11y-keyboard-contract-source-scan` asserts the catch order and
  that the exception has a message. It does not exercise the Avalonia save command.
- **Not covered:** no native test drives Save → external edit → status bar text → Save As. No full-disk or failed-rename
  run against a real volume. Status in the journey file: Skip for the native step.
- **Manual:** open a file, change it in another editor, edit and Save in SQLoryn. Expect a refusal, the other
  editor's bytes intact, and the tab still dirty.

## 3. Connect autofill event ordering (PR 7 finding 2)

`ConnectAutofillController` is UI-independent; the Core tests cover the sequences the window forwards to it:

| Sequence | Core evidence |
|---|---|
| Select profile → late Keychain result | `ctl-keychain-then-change-host`, `autofill-session-happy-path` |
| Typed password, then late result | `ctl-typed-then-late-keychain`, `autofill-typed-replacement-*` |
| Host/port/login/trust change, then late result | `ctl-change-host-then-late-keychain`, `autofill-delayed-a-to-b`, `autofill-identity-and-trust-changes-clear-secret` |
| Profile A → B, late A | `ctl-connect-never-sees-other-secret`, `autofill-profile-switch` |
| Delayed store | `ctl-delayed-store-sequence` |
| Keychain denial for the current lookup; typing over an autofilled value; Forget then late result | `ctl-denied-typed-forget-ordering` (added with this checklist), `autofill-keychain-denial`, `autofill-forget` |

Not covered: the real Avalonia `ConnectWindow` event wiring and the real Keychain. Both remain manual/Skip.

## 4. Launch measurement

`scripts/measure_app_launch.sh [RUNS]` times `open` until a `Sqloryn` process exists and writes
`docs/evidence/launch-<arch>.md` with `uname -m`, macOS version, hardware model, per-run times, median and max. If the
bundle is missing, a `Sqloryn` process is already running, `open` fails or the process never appears, it writes a skip
and the reason instead of numbers. It measures process start only, not a visible window or input readiness, and it is
not disk-cold. Latest result: [evidence/launch-arm64.md](evidence/launch-arm64.md).

## 5. Large-script responsiveness

Core test `large-script-settext-and-find` builds a script of at least 2,000,000 characters, then checks that
`ScriptDocument.SetText`, `FindReplaceEngine.FindNext` (hit at the end) and a wrapping miss each finish within 2 s.
On failure the exception message includes the elapsed milliseconds. This bounds the model operations only; it does
not measure typing, scrolling, syntax work or rendering in the Avalonia editor control.
