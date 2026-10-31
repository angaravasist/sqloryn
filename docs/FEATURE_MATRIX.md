# SSMS compatibility matrix — starting inventory

This is an **initial outline, not a complete SSMS feature inventory**. Every menu, context action, wizard, keyboard shortcut and administration pathway must eventually be catalogued and verified.

| Category | Examples | SQLoryn status |
|---|---|---|
| Connectivity | SQL auth, Entra ID, TLS, connection profiles | Preview: SQL auth + mandatory TLS (session-only); Entra/profiles not implemented |
| Object Explorer | Instances, databases, tables, views, procs, functions | Preview: databases/schemas/tables/views/columns (read-only); live evidence pending |
| Query editing | T-SQL IntelliSense, multiple tabs, scripts | Preview: tabs + UTF-8 scripts; no IntelliSense |
| Results | Grid, messages, multiple result sets, export | Preview: text results + messages; first set shown; no export |
| Query diagnostics | Plans, profiling, statistics, cancellation | Preview: cancel request; no plans |
| Data editing | Edit rows, generate scripts, table designer | Not implemented |
| Security administration | Logins, roles, permissions | Not implemented |
| Schema management | Compare, import/export, DACPAC, BACPAC | Not implemented |
| Operations | Backup/restore, server/database configuration | Not implemented |
| Agent | Jobs, schedules, history, operators | Not implemented |
| Advanced administration | Availability groups, replication, maintenance | Not implemented |
| BI tooling | SSIS, SSAS and SSRS integration | Not implemented |

## Evidence rule

Mark a feature as working only after a reproducible test is performed on the relevant macOS architecture and SQL Server version. Track version-specific limitations and platform restrictions.

## Active work

- [SPEC-001](specs/001-sqlclient-probe.md) probe: Linux E2 historically verified;
  Mac-to-server probe path still needs an approved host ([ADR-002](decisions/002-macos-ssh-sql-probe.md)).
- [SPEC-002](specs/002-developer-preview-0.md) Avalonia app ([ADR-003](decisions/003-desktop-avalonia.md)):
  locally launchable; live desktop SQL walkthrough **unverified**.
- Authoritative exclusions and commands: [STATE.md](STATE.md).
