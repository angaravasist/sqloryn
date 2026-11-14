# ADR-004: Structure of the database studio

Date: 2026-11-03.

## Decision

- Extend the single-process Avalonia app and the `Sqloryn.Core` library. No new process boundary.
- Store non-secret connection profiles in Application Support. Passwords go in the macOS Keychain (service `org.sqloryn.app`) only when the user asks.
- Run `GO`-separated batches one after another in `SqlSession`, with the same cancellation and timeout handling as a single batch.
- Show results in Avalonia DataGrid controls. Export through Core CSV/JSON writers.
- Table design and row editing generate SQL in Core, show it for confirmation and apply it in one transaction. Row updates use optimistic concurrency.
- Use an original app icon and package with `scripts/build_macos_app.sh`.
- The SSH test tooling ([ADR-002](002-macos-ssh-sql-probe.md)) is not a connection feature of the app.
