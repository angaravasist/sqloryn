# SSMS compatibility matrix — starting inventory

This is an **initial outline, not a complete SSMS feature inventory**. Every menu, context action, wizard, keyboard shortcut and administration pathway must eventually be catalogued and verified.

| Category | Examples | SQLoryn status |
|---|---|---|
| Connectivity | SQL auth, Entra ID, TLS, connection profiles | Not implemented |
| Object Explorer | Instances, databases, tables, views, procs, functions | Not implemented |
| Query editing | T-SQL IntelliSense, multiple tabs, scripts | Not implemented |
| Results | Grid, messages, multiple result sets, export | Not implemented |
| Query diagnostics | Plans, profiling, statistics, cancellation | Not implemented |
| Data editing | Edit rows, generate scripts, table designer | Not implemented |
| Security administration | Logins, roles, permissions | Not implemented |
| Schema management | Compare, import/export, DACPAC, BACPAC | Not implemented |
| Operations | Backup/restore, server/database configuration | Not implemented |
| Agent | Jobs, schedules, history, operators | Not implemented |
| Advanced administration | Availability groups, replication, maintenance | Not implemented |
| BI tooling | SSIS, SSAS and SSRS integration | Not implemented |

## Evidence rule

Mark a feature as working only after a reproducible test is performed on the relevant macOS architecture and SQL Server version. Track version-specific limitations and platform restrictions.
