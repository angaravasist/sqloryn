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

## Active bounded experiment

[SPEC-001](specs/001-sqlclient-probe.md) defines a disposable SqlClient
connection/query conformance probe (CAP-002, CAP-013, CAP-068–074, CAP-092–095).
Status: **E2 verified for the bounded local Linux x64 experiment**; native Mac
arm64/x64 SQL E2 **unverified**. [Current-branch Linux regression](https://github.com/angaravasist/sqloryn/actions/runs/41194422292)
at `8285892` passed the refactored PROBE-10 path; the
[earlier Linux evidence](https://github.com/angaravasist/sqloryn/actions/runs/41188623012)
records SQL Server 2022 Developer 16.0.4305.5, SqlClient 7.1.1, .NET 10.0.14; SQL auth,
exact certificate pinning, multiple/empty results and fidelity, notices/late
errors, timeout, observed WAITFOR cancellation/session reuse and 10,000 rows.
Hosted Mac arm64/x64 and Linux component jobs each passed 35 C# assertions and
the Python safety suite of that revision. PROBE-11 adds SSH orchestration and
unit tests; live Mac-to-server runs await an approved host
([ADR-002](decisions/002-macos-ssh-sql-probe.md)). [Evidence and exclusions](STATE.md)
remain authoritative. No desktop or release qualification exists. This does not
promote any application capability above “Not implemented”.
