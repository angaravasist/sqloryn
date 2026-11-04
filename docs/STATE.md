# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-11-04. Experimental; no release support.

## Live Linux Core evidence on 496b503 — 4 November 2026

The disposable SQL integration job passed on tip `496b503` (SQL Server 16.0.4305.5 Developer, GitHub-hosted Linux x64). **CORE-ACCEPTANCE COMPLETE pass=12 skip=0 fail=0**, plus the transport probe and PROBE-10 cleanup. [Run 41807915637](https://github.com/angaravasist/sqloryn/actions/runs/41807915637)

| Scenario | Observed result |
|---|---|
| CORE-MATERIALISE | date/money/smallmoney, NULL, negative, fractional and extreme values |
| CORE-EXPORT | Materialised values preserved in CSV and JSON |
| CORE-DESIGN-PK | CREATE TABLE and composite PK observed independently |
| CORE-EDIT-CONFLICT | Case-only conflict refused; fresh update committed; batch rollback |
| CORE-PLAN-INSERT | Estimated INSERT returned a plan and inserted no row |
| CORE-PLAN-DENIED | Restricted-principal estimate denied and inserted no row |
| CORE-TIME-GUID | time and uniqueidentifier materialise from a second session |
| CORE-DESIGN-APPLY | ApplyTableDesignAsync create/add, failed-batch rollback, stale fingerprint refuse |
| CORE-EDIT-INSERT-DELETE | Insert/delete conflict rolls back; fresh delete commits; second session verifies |
| CORE-CSV-IMPORT | Create/Append exact values past preview sample; fidelity/fingerprint refusals in harness |
| CORE-SECURITY | GRANT preview not executed; second session sees unchanged permissions |
| CORE-BACKUP | COPY_ONLY backup recorded in msdb (or honest server refuse) |

Prior six-scenario pass on `0af9108`: [run 41793052872](https://github.com/angaravasist/sqloryn/actions/runs/41793052872). Mac component success does not imply live Mac SQL or native UI acceptance. Agent and native Mac paths still need their own evidence. Contract acceptance remains open.

## Active assignment

[Delivery contract](ssms/DELIVERY-CONTRACT.md) + [Mac experience](ssms/MAC-EXPERIENCE.md) supersede incremental follow-ups ([audit](ssms/CODEBASE-AUDIT.md), [queue](ssms/DELIVERY-QUEUE.md), [CAP evidence](ssms/CAP_EVIDENCE.csv)). Draft PR #7 remains the single integration PR.

## Work under review

Branch `codex/macos-database-studio` — Draft [PR #7](https://github.com/angaravasist/sqloryn/pull/7).
Full delivery contract active on tip `496b503`. Linux Core live: **12/12** scenarios on that tip. **Not product-complete:** native Mac SQL/UI acceptance, Entra, multi-node HA, BI services, signing, Agent live, and remaining CAP rows stay open.

## Hosted checks

| Tip | Result |
|---|---|
| `06bc2ad` | probe-components ×3 PASS · repo-safeguards PASS · CodeQL PASS · sql-integration SKIPPED — [components](https://github.com/angaravasist/sqloryn/actions/runs/41750374026) · [CodeQL](https://github.com/angaravasist/sqloryn/actions/runs/41750368912) |
| `0af9108` | probe-components ×3 PASS · repo-safeguards PASS · CodeQL PASS · **sql-integration PASS** (six CORE scenarios, cleanup) — [run 41793052872](https://github.com/angaravasist/sqloryn/actions/runs/41793052872) |
| `496b503` | probe-components ×3 PASS · repo-safeguards PASS · **sql-integration PASS** (twelve CORE scenarios, pass=12 skip=0 fail=0, cleanup) — [run 41807915637](https://github.com/angaravasist/sqloryn/actions/runs/41807915637) | Independent verifier: Release 0/0, Core **227 PASS** |
| `11c992b` | Continues from `496b503` with CAP-040/054/060/071 + CAP-084/075/087/099/014; Core **234 PASS** locally; SQL evidence still the twelve scenarios on `496b503` until re-run |

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
| 18 | **Partial** | Inventory present; SPEC-003 CAP anchors realigned; many catalogue rows still unassessed for native/live. sql-integration **PASS** on `496b503` (12 CORE). CAP-073/074/094 remain partial_code (not transport-only). |

## Integrated local checks (post D2–D8 + OE/plan merge)

| Check | Result |
|---|---|
| `dotnet build SQLoryn.slnx -c Release` | 0 warnings / 0 errors on `496b503` |
| Core component executable | **227 PASS** lines (named assertions + summary), 0 FAIL |
| SqlClientProbe.Tests | **35 PASS** (CI matrix on `496b503`) |
| Python probe-runner tests | **48 OK** |
| `repo_preflight.py` | PASS (CI repo-safeguards) |
| `bash scripts/build_macos_app.sh` | arm64/Intel FD `.app` on CI Mac jobs |
| Launch / screenshots | Screen Recording **denied** (`CGPreflightScreenCaptureAccess` false, rechecked unsandboxed 4 Nov 2026). Accessibility **available** (`AXIsProcessTrusted` true for this agent process). Agent-shell Avalonia RenderTimer `-6661` on direct exec; prefer `open` + AX tree over claiming universal screenshot block |
| Live disposable SQL (Linux) | **PASS on 496b503** — twelve CORE scenarios (pass=12 skip=0 fail=0) — [run 41807915637](https://github.com/angaravasist/sqloryn/actions/runs/41807915637). Offline journey ledger: [JOURNEY_EVIDENCE](ssms/JOURNEY_EVIDENCE.md) |
| Native Mac SQL / screenshots | **Not accepted** — Screen Recording still denied; AX available. Linux live evidence ≠ Mac native |
| Windows SSH product path | **Closed** |

## Core SQL acceptance harness

`tests/Sqloryn.Core.SqlAcceptance` runs after a passing `SqlClientProbe` inside
`scripts/run_sql_probe.py` (same licence gate, same disposable instance). **A green
probe alone is not Core acceptance.** Linux evidence on `496b503` closed twelve
named scenarios (table above). Agent remains without a live scenario.

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
