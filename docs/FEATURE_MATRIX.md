# SSMS compatibility matrix — starting inventory

This is an **initial outline, not a complete SSMS feature inventory**. Every menu, context action, wizard, keyboard shortcut and administration pathway must eventually be catalogued and verified. Status here is product honesty, not marketing.

| Category | Examples | SQLoryn status |
|---|---|---|
| Connectivity | SQL auth, Entra ID, TLS, connection profiles | Preview: SQL auth + mandatory TLS; saved profiles (no secrets in JSON); optional Keychain remember-password; Entra not implemented |
| Object Explorer | Instances, databases, tables, views, procs, functions | Preview: databases → schemas → tables/views/procs → columns; properties read-only; live expand evidence pending |
| Query editing | T-SQL IntelliSense, multiple tabs, scripts, GO | Preview: tabs + UTF-8 scripts + find/replace + GO batches; no IntelliSense |
| Results | Grid, messages, multiple result sets, export | Preview: DataGrid per set + messages; CSV/JSON export to one chosen file with staged atomic replace (PR #7 finding 4); ordinal-column JSON; opt-in spreadsheet-safe CSV; script save staged the same way (finding 5); live fidelity evidence pending |
| Query diagnostics | Plans, profiling, statistics, cancellation | Preview: cancel + outcome mapping; no plans |
| Data editing | Edit rows, generate scripts, table designer | Preview: Design Table (DDL preview/confirm) + Edit Top 200 (one transaction, all-or-nothing; refused without key + rowversion/comparable columns or with INSTEAD OF triggers; oversized cells read-only, never written back); component-tested only, live apply evidence pending |
| Security administration | Logins, roles, permissions | Not implemented |
| Schema management | Compare, import/export, DACPAC, BACPAC | Not implemented |
| Operations | Backup/restore, server/database configuration | Not implemented |
| Agent | Jobs, schedules, history, operators | Not implemented |
| Advanced administration | Availability groups, replication, maintenance | Not implemented |
| BI tooling | SSIS, SSAS and SSRS integration | Not implemented |

## Reference coverage

Use the [pinned SSMS comparison](ssms/README.md) for the reference-side inventory, [actions](ssms/actions.csv) for individual candidate behaviours and [crosswalk](ssms/cap-crosswalk.csv) for all 192 CAP IDs. The categories above describe SQLoryn progress. A documented SSMS command is not evidence that SQLoryn implements it.

## Evidence rule

Mark a feature as working only after a reproducible test is performed on the relevant macOS architecture and SQL Server version. Track version-specific limitations and platform restrictions. See [STATE.md](STATE.md).

## Active work

- [SPEC-003](specs/003-macos-database-studio.md) / [ADR-004](decisions/004-macos-database-studio.md): integrated studio on `codex/macos-database-studio`.
- [SPEC-002](specs/002-developer-preview-0.md) remains the DP0 baseline (merged PR #6).
- Optional Mac SSH lab tooling ([ADR-002](decisions/002-macos-ssh-sql-probe.md)) is **not** a product connection feature and is not recreated in the studio UI.
