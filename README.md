# SQLoryn

**SQLoryn** is an independent, open-source project exploring a macOS-first alternative to SQL Server Management Studio.

> **Status: technical discovery.** No database functionality or installation builds are available yet. SQLoryn is not affiliated with Microsoft or SQL Server Management Studio.

## Goals

- Familiar SQL Server administration workflows on macOS.
- Accurate, testable compatibility rather than visual-only imitation.
- Local-first connections with credentials secured in macOS Keychain.
- Reuse permissively licensed, redistributable SQL tooling where feasible.
- Safe defaults for destructive operations, updates, dependencies and CI.

## First engineering milestone

Investigate SQL Tools Service, Microsoft's VS Code MSSQL extension, SqlClient, and DacFx on macOS. Compare Tauri 2 plus a .NET sidecar with a Code-OSS-based approach; build an isolated, reproducible connection/query proof of concept before selecting a desktop framework.

See [architecture](docs/ARCHITECTURE.md), [compatibility matrix](docs/FEATURE_MATRIX.md), [contribution guide](CONTRIBUTING.md) and [security policy](SECURITY.md).

## Security and privacy

Do not put secrets, connection strings, private certificates, live data, database backups, or proprietary binaries in issues, pull requests, logs or commits. SQLoryn must not send queries, results, schemas or credentials to third-party AI services by default.

## Licence

Original SQLoryn code is available under the [MIT License](LICENSE). Third-party dependencies and assets must be evaluated individually for licence compatibility and redistributability.
