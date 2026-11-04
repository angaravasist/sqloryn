# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-11-04. Experimental; no release support.

## Work under review

Branch `codex/macos-database-studio` — Draft [PR #7](https://github.com/angaravasist/sqloryn/pull/7).
Prior tip with hosted green checks: `78fba00`. Follow-up tip under push after D2–D8 + OE/plan merge.

## Hosted checks on `78fba00`

| Check | Result |
|---|---|
| probe-components (ubuntu / macos-15 / macos-15-intel) | PASS |
| repo-safeguards · CodeQL · Analyze (csharp/python/actions) | PASS |
| sql-integration | **SKIPPED** |
| Runs | [components](https://github.com/angaravasist/sqloryn/actions/runs/41739385983) · [CodeQL](https://github.com/angaravasist/sqloryn/actions/runs/41739380738) |

Hosted checks for the post-merge tip are pending after push.

## PR #7 finding dispositions

| # | Disposition | Evidence / gap |
|---|---|---|
| 1 | **Partial** | Hostile DDL warnings separated; live hostile apply **unverified** |
| 2 | **Partial (D8)** | `ConnectAutofillController` + `TryApplySecret` event-sequence tests PASS; native Avalonia window sequence **unverified** |
| 3 | **Partial (D3/D4)** | BIN2 character concurrency SQL + dispose-preserving batch outcomes PASS (fakes); live collation/transaction **unverified** |
| 4 | **Partial** | Export types + single destination; await independent D1 recheck on this tip |
| 5 | **Partial (D2)** | External edit refuses Save; on-disk external bytes preserved — Core `d2-external-edit-survives-save` PASS; UI Save As force path **unverified** |
| 6 | **Partial** | Bounded QueryRaw; live MAX **unverified** |
| 7 | **Partial (D5)** | Selection rejections map to document line — Core `d5-*` PASS; editor caret jump **unverified** |
| 8–12 | **Partial** | Shell build-only |
| 13 | **Partial** | New-tab script SELECT |
| 14 | **Partial** | Keys/indexes/functions folders + truncation; live expand **unverified** |
| 15 | **Partial** | PK create preview + estimated plan fakes prove INSERT never executes on deny/success path; S-1 unexpected exception now always attempts SHOWPLAN OFF + taints session (`estimate-plan-unexpected-exception-turns-off`); live SHOWPLAN **unverified**; S-2 date/money materialisation still open |
| 16 | **Partial** | FD package + icon required |
| 17 | **Open** | No VoiceOver / timings / screenshots (capture blocked) |
| 18 | **Partial** | Inventory present; sql-integration still skipped |

## Integrated local checks (post D2–D8 + OE/plan merge)

| Check | Result |
|---|---|
| `dotnet build SQLoryn.slnx -c Release` | 0 warnings / 0 errors |
| Core component executable | **98 PASS**, 0 FAIL (includes S-1 fix) |
| SqlClientProbe.Tests | **35 PASS** |
| Python tests | **43 OK** |
| `repo_preflight.py` | PASS |
| `bash scripts/build_macos_app.sh` | arm64 FD `.app` |
| Launch smoke | process started |
| Screenshots | **Blocked** (no Screen Recording grant for agent) |
| Live disposable SQL / eight journeys | **Not run** — no authorised fixture |
| Windows SSH product path | **Closed** |

## Reviewer launch

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore -c Release
dotnet run --project tests/Sqloryn.Core.Tests -c Release --no-build
bash scripts/build_macos_app.sh
open artifacts/macos/osx-arm64/SQLoryn.app
```

## Result type coverage (S-2)

`date` (ISO `yyyy-MM-dd`), `money` and `smallmoney` (four fractional digits via `SqlMoney`, invariant, sign preserved)
are materialised by `ResultMaterializer` and covered by fixture tests (`s2-date-money-types`). Live `SqlDataReader`
behaviour against SQL Server for these types is not yet evidenced; `time`, `uniqueidentifier`, `sql_variant` etc.
remain unsupported (throw `NotSupportedException`).

## Explicit non-claims

Not SSMS-compatible; not signed/notarised; AI assistant deferred; no Windows SSH product path.
