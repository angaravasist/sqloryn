# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-11-04. Experimental; no release support.

## Live Linux Core evidence on 0af9108 — 4 November 2026

The disposable SQL integration job passed on SQL Server 16.0.4305.5 Developer edition, on a GitHub-hosted Linux x64 runner. All six named Core scenarios passed with zero skips and zero failures. The transport probe also passed, and PROBE-10 verified removal of its container, network and temporary files. [Run 41793052872](https://github.com/angaravasist/sqloryn/actions/runs/41793052872)

The same workflow completed successfully for Linux, Mac arm64 and Mac Intel component jobs and repository safeguards. Mac component success does not imply live Mac SQL or native UI acceptance.

| Scenario | Observed result |
|---|---|
| CORE-MATERIALISE | date/money/smallmoney, NULL, negative, fractional and extreme values passed |
| CORE-EXPORT | Materialised values preserved in CSV and JSON |
| CORE-DESIGN-PK | Generated CREATE TABLE and composite PK observed independently |
| CORE-EDIT-CONFLICT | Case-only conflict refused, fresh update committed, second-row conflict rolled back first write |
| CORE-PLAN-INSERT | Estimated INSERT returned a plan and inserted no row |
| CORE-PLAN-DENIED | Restricted-principal estimate denied and inserted no row |

This supersedes older statements that no live Core run exists, for these scenarios on this Linux target only. The harness now also names `CORE-TIME-GUID`, `CORE-DESIGN-APPLY`, `CORE-EDIT-INSERT-DELETE`, `CORE-CSV-IMPORT`, `CORE-SECURITY` and `CORE-BACKUP`. Those six were not executed against SQL Server in the change that added them, so they are not live evidence. Agent and native Mac paths still need their own evidence. No native Mac SQL qualification or full-workflow acceptance is implied.

## Active assignment

[Delivery contract](ssms/DELIVERY-CONTRACT.md) + [Mac experience](ssms/MAC-EXPERIENCE.md) supersede incremental follow-ups ([audit](ssms/CODEBASE-AUDIT.md), [queue](ssms/DELIVERY-QUEUE.md), [CAP evidence](ssms/CAP_EVIDENCE.csv)). Draft PR #7 remains the single integration PR.

## Work under review

Branch `codex/macos-database-studio` — Draft [PR #7](https://github.com/angaravasist/sqloryn/pull/7).
Full delivery contract active on tip after WS1–WS12 merges. **Not product-complete:** expanded live scenarios beyond the six on `0af9108`, native Mac SQL/UI acceptance, Entra, multi-node HA, BI services, signing, and remaining CAP rows stay open.

## Hosted checks

| Tip | Result |
|---|---|
| `06bc2ad` | probe-components ×3 PASS · repo-safeguards PASS · CodeQL PASS · sql-integration SKIPPED — [components](https://github.com/angaravasist/sqloryn/actions/runs/41750374026) · [CodeQL](https://github.com/angaravasist/sqloryn/actions/runs/41750368912) |
| `0af9108` | probe-components ×3 PASS · repo-safeguards PASS · CodeQL PASS · **sql-integration PASS** (six CORE scenarios, cleanup) — [run 41793052872](https://github.com/angaravasist/sqloryn/actions/runs/41793052872) |

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
| 7b | **Partial (WS3-T1)** | SQL editor is AvaloniaEdit ([ADR-005](decisions/005-sql-editor-component.md)): line numbers, lexical T-SQL colouring, Go to Line (Ctrl+G); Core `ws3-*` + source scans PASS, headless highlighter [evidence](evidence/ws3-editor-highlighting.md); native rendering, caret jump and VoiceOver **unverified** |
| 8–12 | **Partial** | Shell build-only |
| 13 | **Partial** | New-tab script SELECT |
| 14 | **Partial** | Keys/indexes/functions folders + truncation; live expand **unverified** |
| 15 | **Partial** | PK create preview + estimated plan fakes prove INSERT never executes on deny/success path; S-1 unexpected exception now always attempts SHOWPLAN OFF + taints session (`estimate-plan-unexpected-exception-turns-off`); plan reads bounded per plan (4 Mi chars) and in total (8 Mi chars) with SHOWPLAN reset taint on unconfirmed OFF (`plan-reader-*`, `estimate-plan-total-budget*`); live SHOWPLAN **unverified**; S-2 date/money materialisation closed against **fixtures only** (`s2-*`), live reader behaviour **unverified** |
| 16 | **Partial** | FD package + icon required |
| 17 | **Partial** | Process-start launch median/max recorded ([launch-arm64](evidence/launch-arm64.md)); large-script SetText/FindNext bound PASS; keyboard AutomationProperties source contract PASS; VoiceOver walk and screenshots still **blocked** (no Screen Recording) |
| 18 | **Partial** | Inventory present; SPEC-003 CAP anchors realigned and the edit-row mapping now reaches CAP-124 (catalogue rows with evidence cite test names; rows without evidence returned to unassessed); J7 recorded as partial, not substituted by redaction; 140 of 192 catalogue rows remain unassessed and CAP-073/074/094 still say transport experiment only (no application evidence cited); sql-integration still skipped |

## Integrated local checks (post D2–D8 + OE/plan merge)

| Check | Result |
|---|---|
| `dotnet build SQLoryn.slnx -c Release` | 0 warnings / 0 errors |
| Core component executable | **217 PASS** lines (named assertions + summary), 0 FAIL |
| SqlClientProbe.Tests | **35 PASS** (prior tip; re-run with integrated build as needed) |
| Python tests | **51 OK** |
| `repo_preflight.py` | PASS |
| `bash scripts/build_macos_app.sh` | arm64 FD `.app` + [provenance](evidence/package-arm64.md) |
| Launch / screenshots | Screen Recording **denied** (`CGPreflightScreenCaptureAccess` false, rechecked unsandboxed 4 Nov 2026). Accessibility **available** (`AXIsProcessTrusted` true for this agent process). Agent-shell Avalonia RenderTimer `-6661` on direct exec; prefer `open` + AX tree over claiming universal screenshot block |
| Live disposable SQL (Linux) | **PASS on 0af9108** for the six scenarios listed above — [run 41793052872](https://github.com/angaravasist/sqloryn/actions/runs/41793052872). This wave's extra scenario IDs are in the harness and are **not** a live pass until that job runs again. Offline journey ledger: [JOURNEY_EVIDENCE](ssms/JOURNEY_EVIDENCE.md) |
| Native Mac SQL / screenshots | **Not accepted** — Screen Recording still denied; AX available. Linux live evidence ≠ Mac native |
| Windows SSH product path | **Closed** |

## Core SQL acceptance harness

`tests/Sqloryn.Core.SqlAcceptance` runs after a passing `SqlClientProbe` inside
`scripts/run_sql_probe.py` (same licence gate, same disposable instance). **A green
probe alone is not Core acceptance.** Linux evidence on `0af9108` closed the six
named scenarios in the table above only. The harness now also runs
`CORE-TIME-GUID`, `CORE-DESIGN-APPLY`, `CORE-EDIT-INSERT-DELETE`, `CORE-CSV-IMPORT`
(required), and `CORE-SECURITY` / `CORE-BACKUP` (pass or an explicit skip). That
wiring is not itself a live pass. Agent remains without a live scenario.

## Independent review on 4 November 2026

See [CODEBASE-AUDIT](ssms/CODEBASE-AUDIT.md) for the 06bc2ad source audit, independently repeated Release/component/Python checks and limited native offline observations. The inspected existing package differs from the fresh build, so its UI observations have separate provenance. These checks add no live SQL evidence. Continue through the [full delivery contract](ssms/DELIVERY-CONTRACT.md).

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
are materialised by `ResultMaterializer` and covered by fixtures (`s2-date-money-types`) **and** live Linux
`CORE-MATERIALISE` / `CORE-EXPORT` on `0af9108`. `time` and `uniqueidentifier` have component fixtures (`ws4-*`)
and a harness scenario `CORE-TIME-GUID`; live reader evidence for those types is still pending a disposable run.
`sql_variant` and other unhandled types still throw
`NotSupportedException`.

## WS1-T1 connections (component-verified only)

- Database names are passed only via `SqlConnectionStringBuilder` and round-trip checked; brackets, spaces, Unicode,
  quotes, `;` and `=` are accepted. Remaining exclusions: empty/whitespace-only, over 128 characters, control characters.
- `ConnectionCapabilities.ProbeAsync` / `SqlSession.ProbeCapabilitiesAsync` (opt-in, not run by connect): SHOWPLAN
  permission and SQL Server Agent are `null` (unknown) until observed; denial or failure never faults the session.
  No live SQL Server evidence yet.
- ConnectWindow shows a TLS summary and an auth dropdown: SQL auth enabled; Entra ID and Integrated/Kerberos disabled
  with tooltips naming the missing dependency (no provider implemented).
- `ConnectionProfile.Group` is optional; legacy `profiles.json` loads with `Group = null`.

## Explicit non-claims

Not SSMS-compatible; not signed/notarised; AI assistant deferred; no Windows SSH product path.

## WS10-T1 / WS11-T1 gated shells

Tools menu entries open honest gated windows (`GatedWorkspaceWindow`, model in `Sqloryn.Core/GatedWorkspace.cs`) for high availability, replication and BI/cloud. They are explanations only: no server read, no health or topology claim, no credential field, no cloud call. HA/replication stay incomplete until an isolated multi-node fixture verifies the real operation and recovery; SSISDB/SSAS/SSRS/Fabric/Azure stay disabled until each protocol and authentication path exists. CAP-157/158/163–168/170/171 remain `missing`/`unverified` in [CAP_EVIDENCE](ssms/CAP_EVIDENCE.csv). Component evidence only (source scans, no native UI walk).
