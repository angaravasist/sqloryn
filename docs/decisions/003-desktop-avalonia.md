# ADR-003: Avalonia single-process desktop app

Date: 2026-10-31.

## Decision

| Option | Assessment |
|---|---|
| Avalonia + .NET 10, single process | MIT-licensed UI. SqlClient runs in the same process, so cancel and dispose share one lifetime. Simplest `.app` packaging. **Chosen.** |
| Native AppKit/SwiftUI + local .NET helper | Better Mac chrome, but needs a private IPC channel, two packages and a credential boundary between processes. |
| .NET MAUI / WinUI-oriented stacks | Larger platform surface and redistribution review for little benefit in a Mac SQL editor. |
| Electron / web shell | Adds a Chromium runtime and IPC with no driver advantage. |

The riskiest boundary is in `src/Sqloryn.Core`: a window owns a `SqlSession`, runs SQL, streams bounded results, cancels via `CancellationToken` and disposes without leaking connections. Component tests cover it. With no separate driver process, IPC crash handling is not needed.

## Consequences

- Dependencies are Avalonia (MIT) plus SqlClient. SNI assets stay excluded.
- Accessibility uses Avalonia automation peers. VoiceOver behaviour has to be checked on a device.
