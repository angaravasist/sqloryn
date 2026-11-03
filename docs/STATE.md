# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-11-03. Experimental; no release support.

## Work under review

Branch `codex/macos-database-studio` — macOS Database Studio integrated preview
([SPEC-003](specs/003-macos-database-studio.md), [ADR-004](decisions/004-macos-database-studio.md)).
Builds on merged Developer Preview 0 (PR #6 / `f561597`).

## Evidence

| Claim | Status |
|---|---|
| Core modules: GO splitter, profiles, Keychain store, settings, export, metadata SQL, table design preview, edit-rows SQL, diagnostics redaction, find/replace | Automated Core suite PASS (this branch) |
| `SqlSession` sequential GO batches + aggregate results | Automated Core PASS |
| App builds (Release) with studio shell, DataGrid results, connect profiles UI | Local Release build 0 warnings / 0 errors |
| Original icon packaged into `.app` Info.plist | Local `build_macos_app.sh` (arm64 host) |
| Offline UI launch smoke (no crash on open) | Observed briefly; not a full click-through matrix |
| Live SQL: connect / OE expand / GO / export / design / edit-rows | **Unverified on this branch** (no disposable lab in this session; Windows SSH product path not recreated) |
| Prior DP0 arm64 connect + `SELECT 1` via disposable lab | Historical on PR #6; lab torn down; do not treat as current tip evidence |
| Intel Mac desktop SQL | **Unverified** |
| Hosted CI on this branch | Pending after draft PR push |

## Reviewer launch

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
# SDK 10.0.403 per global.json — ~/.dotnet/dotnet if needed
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore -c Release
dotnet run --project tests/Sqloryn.Core.Tests -c Release --no-build
bash scripts/build_macos_app.sh
open artifacts/macos/osx-arm64/SQLoryn.app   # osx-x64 on Intel
```

## Explicit non-claims

- Not SSMS-compatible; not production-ready; not signed/notarised.
- No Windows SSH connection feature in the product UI.
- No Entra / Agent / AG / BI surfaces.
