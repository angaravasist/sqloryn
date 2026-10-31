# SQLoryn

**SQLoryn** is an independent, open-source project exploring a macOS-first alternative to SQL Server Management Studio.

> **Status: technical discovery.** An experimental SqlClient conformance probe is available for disposable test environments. There is no supported macOS application or installation build. SQLoryn is not affiliated with Microsoft or SQL Server Management Studio.

## Goals

- Familiar SQL Server administration workflows on macOS.
- Accurate, testable compatibility rather than visual-only imitation.
- Local-first connections with credentials secured in macOS Keychain.
- Reuse permissively licensed, redistributable SQL tooling where feasible.
- Safe defaults for destructive operations, updates, dependencies and CI.

## First engineering milestone

Validate real connection/query semantics before selecting a desktop framework.
The current experiment uses .NET and SqlClient, with synthetic SQL, strict
certificate pinning, cancellation checks and an isolated SQL Server runner.
SQL Tools Service remains a candidate for richer tooling; the UI framework is
undecided. No test result implies broad SSMS parity.

Start with the [maintainer context map](docs/README.md), [actual evidence and next
work](docs/STATE.md), [build/test guide](docs/TESTING.md) and
[compatibility matrix](docs/FEATURE_MATRIX.md). See also
[contribution guide](CONTRIBUTING.md) and [security policy](SECURITY.md).

## Security and privacy

Do not put secrets, connection strings, private certificates, live data, database backups, or proprietary binaries in issues, pull requests, logs or commits. SQLoryn must not send queries, results, schemas or credentials to third-party AI services by default.

## Licence

Original SQLoryn code is available under the [MIT License](LICENSE). Third-party dependencies and assets must be evaluated individually for licence compatibility and redistributability.
