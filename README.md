# SQLoryn

**SQLoryn** is an independent, open-source, macOS-first SQL Server management application for developers and DBAs.

> **Status: macOS Database Studio preview (draft).** A launchable development `.app` exists on the preview branch. It is not signed, notarised or supported for production. Not SSMS-compatible. SQLoryn is not affiliated with Microsoft or SQL Server Management Studio.

## Build and launch (macOS)

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore --configuration Release
bash scripts/build_macos_app.sh
open artifacts/macos/osx-arm64/SQLoryn.app   # use osx-x64 on Intel Macs
```

Requires .NET SDK 10.0.403 (see `global.json`). Walkthrough: [docs/WALKTHROUGH.md](docs/WALKTHROUGH.md).

## Goals

- Familiar SQL Server workflows on macOS with accurate, testable behaviour.
- Local-first connections; SQL authentication with optional saved profiles and macOS Keychain remember-password (never store secrets in profile JSON).
- Safe defaults for TLS, cancellation, destructive operations and CI.

Start with the [context map](docs/README.md), [STATE.md](docs/STATE.md),
[TESTING.md](docs/TESTING.md) and [FEATURE_MATRIX.md](docs/FEATURE_MATRIX.md).

The [SSMS 22.11.3 comparison inventory](docs/ssms/README.md) lists reference behaviours and gaps. It is separate from verified SQLoryn functionality.

## Licence

Original SQLoryn code is MIT ([LICENSE](LICENSE)). Third-party packages must be evaluated individually; Avalonia and SqlClient terms are summarised in [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md).
