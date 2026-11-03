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
| Export/save safety (PR #7 findings 4–5): single-destination export, loss-aware JSON, spreadsheet-safe CSV option, staged atomic script save; policy in `SafeFileWriter` (symlink write-through, mode preserved, last-writer-wins, no external-edit detection) | Automated Core PASS; native save-sheet/UI path not click-tested |
| `SqlSession` sequential GO batches + aggregate results | Automated Core PASS |
| App builds (Release) with studio shell, DataGrid results, connect profiles UI | Local Release build 0 warnings / 0 errors |
| Original icon packaged into `.app` Info.plist | Local `build_macos_app.sh` (arm64 host) |
| Offline UI launch smoke (no crash on open) | Observed briefly; not a full click-through matrix |
| PR #7 findings 3 & 6 (edit-rows atomic apply; QueryRaw byte budget) | Core component tests PASS with fakes (`edit-rows-batch`, `edit-rows-policy`, `query-raw-budget`); real SqlTransaction path, trigger behaviour, `GetChars` on xml/MAX columns and numeric(38) **unverified against SQL Server** |
| Live SQL: connect / OE expand / GO / export / design / edit-rows | **Unverified on this branch** (no disposable lab in this session; Windows SSH product path not recreated) |
| Prior DP0 arm64 connect + `SELECT 1` via disposable lab | Historical on PR #6; lab torn down; do not treat as current tip evidence |
| Intel Mac desktop SQL | **Unverified** |
| Hosted CI on this branch | Component CI passed on macOS arm64, macOS Intel and Ubuntu. Repository safeguards and CodeQL passed. SQL integration was skipped. [CI](https://github.com/angaravasist/sqloryn/actions/runs/41718868380) · [CodeQL](https://github.com/angaravasist/sqloryn/actions/runs/41718863474) |

## Comparator research

The [SSMS 22.11.3 inventory](ssms/README.md) adds reference-side research and concurrent feature ownership. The Windows binary and optional service combinations have not been independently swept. This documentation update ran no new SQL qualification and changes no supported-platform claim.

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
