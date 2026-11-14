# ADR-004 — macOS Database Studio integrated preview

**Status:** Accepted for branch `codex/macos-database-studio`  
**Date:** 2026-11-03  
**Base:** `origin/main` including merged Developer Preview 0 (PR #6)

## Context

DP0 proved Avalonia + SqlSession connect/run/cancel. The product brief requires a coherent studio: profiles/Keychain, Object Explorer depth, GO batches, grids/export, limited design/edit-rows, diagnostics, original branding — without claiming SSMS parity or recreating Windows SSH.

## Decision

- Extend the single-process Avalonia app and `Sqloryn.Core` library (no new process boundary).
- Persist non-secret profiles under Application Support; optional passwords in macOS Keychain (`org.sqloryn.app`).
- Execute `GO`-separated batches sequentially in `SqlSession` with existing stop-cause semantics.
- Present results in Avalonia DataGrid(s); export via Core CSV/JSON helpers.
- Table design and edit-rows are preview/confirm gated; optimistic concurrency for updates.
- Original app icon (assets/icon); packaging via `scripts/build_macos_app.sh`.
- Optional PROBE-11 SSH lab tooling remains documentation/scripts only — not a product connection path.

## Consequences

- Live SQL and Intel Mac evidence remain separate gates (see STATE.md).
- Entra/Kerberos/Agent/AG remain unimplemented candidates.
- DataGrid package pinned to 12.1.2 (latest available; works with Avalonia 12.1.4).
