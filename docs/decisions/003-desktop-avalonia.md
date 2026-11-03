# ADR-003 — Avalonia single-process desktop for Developer Preview 0

Provisional for Developer Preview 0 only. Owner: SQLoryn maintainer.
Date: 2026-10-31. Extends [ADR-001](001-sqlclient-probe.md); does not finalise
packaging, accessibility device testing or release signing.

## Decision and alternatives

| Candidate | Assessment |
|---|---|
| Avalonia 11 + .NET 10, single process | MIT UI; SqlClient runs in-process; cancel/dispose share one lifetime; simplest packaging for a development `.app`. **Selected.** |
| Native AppKit/SwiftUI + local .NET helper | Stronger Mac chrome; requires a private IPC channel, dual packaging and credential boundary design. Deferred until Preview 0 proves the workflow. |
| .NET MAUI / WinUI-oriented stacks | Heavier platform surface and redistribution review for little gain on a Mac-first SQL editor. Not selected. |
| Electron / web shell | Extra Chromium redistributable and IPC; no driver advantage. Rejected for this preview. |

Riskiest boundary: a Mac window owns a `SqlSession`, submits SQL, streams bounded
results, cancels via `CancellationToken`, and disposes without leaking
connections. That boundary is implemented in `src/Sqloryn.Core` and exercised by
executable component tests plus a launchable Avalonia shell. No separate driver
process exists in this preview, so IPC crash handling is not required.

## Consequences

- Dependencies: Avalonia (MIT) plus existing SqlClient pins; SNI assets remain
  excluded on Unix. Exact package versions live in lockfiles and DEPENDENCIES.md.
- Accessibility uses Avalonia automation peers; VoiceOver device evidence is
  still required before release claims.
- Switching to a native shell later is an explicit ADR supersession; do not
  treat this preview UI as the permanent Mac design.
