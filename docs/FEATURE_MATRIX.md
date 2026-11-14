# SSMS compatibility matrix — starting inventory

This is an **initial outline, not a complete SSMS feature inventory**. Status here is product honesty, not marketing.

"Live Linux" means a named `tests/Sqloryn.Core.SqlAcceptance` scenario passed on a disposable GitHub-hosted SQL Server 2022 (Linux x64). It exercises Core paths, not the native Mac UI. Run details are in [STATE.md](STATE.md).

| Category | SQLoryn status | Live Linux evidence | Still open |
|---|---|---|---|
| Connectivity | SQL auth, mandatory TLS, saved profiles (no secrets in JSON), optional Keychain remember-password | Connect (every scenario) | Entra, integrated/Kerberos, native Mac SQL |
| Object Explorer | Lazy, bounded tree: databases, types, schemas, tables/views/keys/indexes/procs/functions/sequences/synonyms, column/constraint/trigger folders; read-only Properties; loaded-label filter; denied/failed reads labelled | — | Live expand, search of unloaded objects, full definitions |
| Query editing | Tabs, UTF-8 scripts, find/replace, GO batches, AvaloniaEdit colouring. Unsaved-tab recovery (debounced, off the UI thread) and execution history, both on by default with an off switch, 30-day default retention and Clear in Settings › Privacy | — | IntelliSense, formatting, native recovery sheet walk |
| Results | DataGrid per set, messages with severity labels, running status, binary as hex, XML/JSON viewer, retained-row sort/filter, CSV/JSON export with atomic replace | CORE-MATERIALISE, CORE-TIME-GUID, CORE-EXPORT | `sql_variant` and other unhandled types, live PRINT/varbinary, native viewer |
| Query diagnostics | Cancel; client elapsed time; Display Estimated Plan (SHOWPLAN_XML text, never executes, size-capped, unconfirmed reset taints the session) | CORE-PLAN-INSERT, CORE-PLAN-DENIED | Graphical/actual plans, statistics |
| Data editing | Design Table / New Table with PK (preview, confirm, one transaction, freshness check); Edit Top 200 with update/insert/delete in one transaction | CORE-DESIGN-PK, CORE-DESIGN-APPLY, CORE-EDIT-CONFLICT, CORE-EDIT-INSERT-DELETE | Hostile-DDL live apply, full object designers |
| Security administration | Principal/role/permission reads; allow-listed GRANT/DENY/REVOKE preview; confirmed transactional apply | CORE-SECURITY (preview changes nothing; apply not called) | Live apply, restricted-principal effects, login/user/role mutation |
| Schema management | Flat-file import: preview, mapping, validation, Create/Append/Replace in one transaction | CORE-CSV-IMPORT | Compare, DACPAC/BACPAC |
| Operations | Backup/restore/CHECKDB plans with preview and confirmation | CORE-BACKUP (COPY_ONLY recorded in msdb) | Restore round trip, VERIFYONLY/chain, server configuration |
| Agent | Jobs, schedules, bounded history; Run/Delete gated by confirmation | — | Live Agent, job editing, alerts/operators |
| Advanced administration | Gated shell only (Tools › High Availability / Replication): explanation, no health or topology | — | Not implemented; incomplete until a multi-node topology and recovery are verified (CAP-157/158) |
| BI tooling | Gated shell only (Tools › BI and Cloud): each service disabled, no credentials, no cloud call | — | Not implemented; incomplete until each service is verified (CAP-163–168/170/171) |

## Reference coverage

Use the [pinned SSMS comparison](ssms/README.md), [actions](ssms/actions.csv) and [crosswalk](ssms/cap-crosswalk.csv) for all 192 CAP IDs. A documented SSMS command is not evidence that SQLoryn implements it.

## Evidence rule

Mark a feature as working only after a reproducible test on the relevant macOS architecture and SQL Server version. Live Linux Core evidence does not qualify native Mac SQL or UI. A green `SqlClientProbe` alone is not Core acceptance.

## Active work

- [SPEC-003](specs/003-macos-database-studio.md) / [ADR-004](decisions/004-macos-database-studio.md): integrated studio on `codex/macos-database-studio` (PR #7).
- [SPEC-002](specs/002-developer-preview-0.md) remains the DP0 baseline (merged PR #6).
- Mac SSH lab tooling ([ADR-002](decisions/002-macos-ssh-sql-probe.md)) is not a product connection feature.
