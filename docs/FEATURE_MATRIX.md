# SSMS compatibility matrix — starting inventory

This is an **initial outline, not a complete SSMS feature inventory**. Every menu, context action, wizard, keyboard shortcut and administration pathway must eventually be catalogued and verified. Status here is product honesty, not marketing.

| Category | Examples | SQLoryn status |
|---|---|---|
| Connectivity | SQL auth, Entra ID, TLS, connection profiles | Preview: SQL auth + mandatory TLS; saved profiles (no secrets in JSON); optional Keychain remember-password; Entra not implemented |
| Object Explorer | Instances, databases, tables, views, procs, functions | Preview: databases → schemas → tables/views/keys/indexes/procs/functions/sequences/synonyms folders (lazy, capped reads say when truncated) → table/view folders for columns, foreign keys, check/default constraints, triggers; a newer expansion cancels the previous one; denied/failed reads show `Denied:`/`Error:` node text, not an empty folder (metadata visibility can still hide objects silently); client-side filter over loaded labels; table/view overview (column summary, key/constraint counts) in the inspector; component-tested only (`ExplorerCatalogTests`), live expand evidence pending |
| Query editing | T-SQL IntelliSense, multiple tabs, scripts, GO | Preview: tabs + UTF-8 scripts + find/replace + GO batches; no IntelliSense |
| Results | Grid, messages, multiple result sets, export | Preview: DataGrid per set + messages; CSV/JSON export to one chosen file with staged atomic replace (PR #7 finding 4); ordinal-column JSON; opt-in spreadsheet-safe CSV; script save staged the same way (finding 5); date/money/smallmoney materialise in fixtures only (S-2); live fidelity evidence pending |
| Query diagnostics | Plans, profiling, statistics, cancellation | Preview: cancel + outcome mapping; Query > Display Estimated Plan (SHOWPLAN_XML, XML text in Messages, never executes the SQL; component-tested with fakes only: each plan capped at 4 Mi characters, 8 Mi in total, and an unconfirmed SHOWPLAN reset taints the session until reconnect; live SHOWPLAN evidence pending); no graphical plan, no actual plan, no statistics |
| Data editing | Edit rows, generate scripts, table designer | Preview: Design Table (DDL preview/confirm; apply runs in ONE transaction with a catalog-freshness check, mixed/non-transactional batches refused) and New Table with optional primary key (CREATE TABLE preview/confirm; existing-table key changes are not generated) + Edit Top 200 (one transaction, all-or-nothing, including Add row/Delete row with destructive confirmation where the key policy allows; refused without key + rowversion/comparable columns or with INSTEAD OF triggers; oversized cells read-only, never written back); component-tested only, live apply evidence pending |
| Security administration | Logins, roles, permissions | Preview (read + permission preview only): Tools > Security… (or Object Explorer context menu) lists server logins, server roles, database users and database roles with membership (bounded to 2,000 rows, truncation named, denied reads shown as `Denied:` text rather than an empty list), shows the selected principal's explicit permissions, and previews GRANT/DENY/REVOKE built from an allow-listed structured model (server, database, schema, object, optional columns, grant option, cascade). Preview is a pure function and changes nothing. Confirm Apply… shows the exact SQL and runs it in ONE owned transaction; any failure rolls back, a server refusal is reported as refused with nothing changed, and an unconfirmed commit disconnects the session and says the outcome is unknown. Never reads password hashes; no password or credential field exists; logins are not created, altered or dropped; role membership is read-only; contained users are labelled only; effective (role-inherited) permissions are not computed. Component-tested with fakes only (`SecurityTests`); no live restricted-principal evidence yet; no Windows-domain or tenant claim |
| Schema management | Compare, import/export, DACPAC, BACPAC | Preview (flat-file import only): File > Import Flat File… previews a delimited file (delimiter/header/encoding, quoted newlines, strict encoding, ISO-only date and `.`-only number guesses, formula-like cell warnings) and maps columns; preview and SQL preview never write. Confirm runs the whole file through validation first (bad values and in-file duplicate keys refused with row numbers), then ONE transaction: optional CREATE TABLE or counted DELETE (Replace), parameterised INSERT batches, commit; any error, server duplicate key or cancel rolls back. Component-tested with fakes only; live apply evidence pending. No Create-time keys, no DACPAC/BACPAC, no compare |
| Operations | Backup/restore, server/database configuration | Not implemented |
| Agent | Jobs, schedules, history, operators | Not implemented |
| Advanced administration | Availability groups, replication, maintenance | Not implemented |
| BI tooling | SSIS, SSAS and SSRS integration | Not implemented |

## Reference coverage

Use the [pinned SSMS comparison](ssms/README.md) for the reference-side inventory, [actions](ssms/actions.csv) for individual candidate behaviours and [crosswalk](ssms/cap-crosswalk.csv) for all 192 CAP IDs. The categories above describe SQLoryn progress. A documented SSMS command is not evidence that SQLoryn implements it.

## Evidence rule

`tests/Sqloryn.Core.SqlAcceptance` (Core acceptance on a disposable instance) has been added but **has not run against a live server**; a green `SqlClientProbe` alone is not Core acceptance. Nothing above is marked live-verified.

Mark a feature as working only after a reproducible test is performed on the relevant macOS architecture and SQL Server version. Track version-specific limitations and platform restrictions. See [STATE.md](STATE.md).

## Active work

- [SPEC-003](specs/003-macos-database-studio.md) / [ADR-004](decisions/004-macos-database-studio.md): integrated studio on `codex/macos-database-studio`.
- [SPEC-002](specs/002-developer-preview-0.md) remains the DP0 baseline (merged PR #6).
- Optional Mac SSH lab tooling ([ADR-002](decisions/002-macos-ssh-sql-probe.md)) is **not** a product connection feature and is not recreated in the studio UI.
