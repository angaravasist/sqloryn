# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-11-04. Experimental; no release support.

## Work under review

Branch `codex/macos-database-studio` — macOS Database Studio integrated preview
([SPEC-003](specs/003-macos-database-studio.md), [ADR-004](decisions/004-macos-database-studio.md),
[PR7 acceptance](ssms/PR7-ACCEPTANCE.md)). Builds on merged Developer Preview 0 (PR #6 / `f561597`).
Draft [PR #7](https://github.com/angaravasist/sqloryn/pull/7).

## PR #7 finding dispositions

| # | Disposition | Evidence |
|---|---|---|
| 1 | **Fixed (component)** — design warnings separated from `ExecutableSql` | Core `table-design-preview` / hostile-identifier tests PASS |
| 2 | **Fixed (component)** — Keychain autofill bound to endpoint identity + generation | Core `autofill-*` / `endpoint-identity` PASS; ConnectWindow wired |
| 3 | **Fixed (component)** — transactional edit batch; conflict rolls back set | Core `edit-rows-batch` / `edit-rows-policy` PASS (fakes); live SqlTransaction **unverified** |
| 4 | **Fixed (component)** — single-destination export; ordinal JSON; spreadsheet-safe CSV | Core `result-export-writer` / `json-duplicate-columns-preserved` PASS |
| 5 | **Fixed (component)** — staged atomic script save (`SafeFileWriter`) | Core `safe-file-writer` PASS |
| 6 | **Fixed (component)** — bounded `QueryRaw` / omit oversized cells; no writeback | Core `query-raw-budget` PASS; live MAX/xml **unverified** |
| 7 | **Fixed (component)** — GO/SQLCMD preflight with source spans before any execute | Core `go-parse-source-spans` / `sqlcmd-preflight-rejection` / `sqlsession-preflight-before-execute` PASS |
| 8–12 | **Implemented (build)** — native menu/⌘, welcome, system theme, compact layout, honest settings | Core `app-settings` PASS; UI **not** click/VoiceOver verified |
| 13 | **Fixed (component+wiring)** — Script SELECT opens new tab bound to object DB | Core `script-from-object-plan` PASS; UI **not** click-tested |
| 14 (partial) | **Partial** — truncation honesty in OE; keys/indexes/functions still incomplete | Core `oe-truncation-honesty` PASS |
| 15 | **Open** — new-table-with-PK journey + estimated plan / SHOWPLAN denial | Not implemented this tip |
| 16 | **Partial** — icon required; plist lint; optional `SQLORYN_SELF_CONTAINED=1`; unsigned only | Framework-dependent arm64 package rebuilt; clean-Mac install **unverified**; runtime licence audit for SC **pending** |
| 17 | **Open** — VoiceOver walk + performance measurements | No numbers invented; smoke launch only |
| 18 | **Partial** — STATE/matrix/ssms inventory updated; CAP catalogue still mostly candidates | Hosted component CI previously green on `96f742e`; recheck after this tip |

## Integrated local checks (this tip)

| Check | Result |
|---|---|
| `dotnet build SQLoryn.slnx -c Release` | 0 warnings / 0 errors |
| Core component executable | **71 PASS**, 0 FAIL |
| SqlClientProbe.Tests | **35 PASS** |
| Python `test_*.py` | **43 OK** |
| `repo_preflight.py` | PASS |
| `git diff --check` | clean |
| `bash scripts/build_macos_app.sh` | arm64 `.app` with icon |
| Launch smoke | process started then stopped |
| Live disposable SQL / eight journeys | **Not run** (no authorised fixture this session) |
| Windows SSH product path | **Closed** (not recreated) |

## Explicit non-claims

- Not SSMS-compatible; not production-ready; not signed/notarised.
- No Windows SSH connection feature in the product UI.
- No Entra / Agent / AG / BI surfaces; CAP-176 AI remains deferred.
- AI query assistant remains deferred pending maintainer approval.

## Reviewer launch

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
# SDK 10.0.403 per global.json — ~/.dotnet/dotnet if needed
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore -c Release
dotnet run --project tests/Sqloryn.Core.Tests -c Release --no-build
dotnet run --project tests/SqlClientProbe.Tests -c Release --no-build
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 scripts/repo_preflight.py
bash scripts/build_macos_app.sh
open artifacts/macos/osx-arm64/SQLoryn.app   # osx-x64 on Intel
# optional self-contained (licence audit still pending): SQLORYN_SELF_CONTAINED=1 bash scripts/build_macos_app.sh
```
