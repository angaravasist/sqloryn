# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-11-04. Experimental; no release support.

## Active assignment

[Delivery contract](ssms/DELIVERY-CONTRACT.md) + [Mac experience](ssms/MAC-EXPERIENCE.md) supersede incremental follow-ups ([audit](ssms/CODEBASE-AUDIT.md), [queue](ssms/DELIVERY-QUEUE.md), [CAP evidence](ssms/CAP_EVIDENCE.csv)). Draft PR #7 remains the single integration PR.

## Work under review

Branch `codex/macos-database-studio` — Draft [PR #7](https://github.com/angaravasist/sqloryn/pull/7).
WS7-T1 security (worktree `ws7-security`, not yet merged): `SecurityTests` named assertions PASS with fakes; no live restricted-principal evidence. Full delivery contract active. Tip under push merges WS1–WS6 + WS12 component work (editor, import, explorer, types, transactional DDL, Mac shell/packaging). Not product-complete: live SQL, screenshots, security/ops/Agent/HA/BI queues remain.

## Hosted checks

| Tip | Result |
|---|---|
| `06bc2ad` | probe-components ×3 PASS · repo-safeguards PASS · CodeQL PASS · sql-integration SKIPPED — [components](https://github.com/angaravasist/sqloryn/actions/runs/41750374026) · [CodeQL](https://github.com/angaravasist/sqloryn/actions/runs/41750368912) |
| post-delivery tip | pending after push |

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
| Core component executable | **169 PASS** lines (named assertions + summary), 0 FAIL |
| SqlClientProbe.Tests | **35 PASS** (prior tip; re-run with integrated build as needed) |
| Python tests | **51 OK** |
| `repo_preflight.py` | PASS |
| `bash scripts/build_macos_app.sh` | arm64 FD `.app` + [provenance](evidence/package-arm64.md) |
| Launch / screenshots | **Blocked** — Screen Recording denied; agent-shell Avalonia RenderTimer `-6661` on direct exec |
| Live disposable SQL / eight journeys | **Not run** — CORE harness present; offline journey ledger in [JOURNEY_EVIDENCE](ssms/JOURNEY_EVIDENCE.md) |
| Windows SSH product path | **Closed** |

## Core SQL acceptance harness

`tests/Sqloryn.Core.SqlAcceptance` runs after a passing `SqlClientProbe` inside
`scripts/run_sql_probe.py` (same licence gate, same disposable instance, local and
SSH modes). **A green probe alone is not Core acceptance** and closes no Core
finding. Added here, but **not yet run against a live SQL Server** (no Docker
engine in the authoring environment): findings 3 (live collation/transaction),
6, 15 and S-2 (live date/money reads) stay open until a `CORE-*` transcript from a
real run is recorded. S-2 is fixed in `ResultMaterializer` and fixture-tested, so
`CORE-MATERIALISE` / `CORE-EXPORT` are no longer expected to fail on the missing
`date`/`money` handling, but that expectation is a prediction, not evidence: no
acceptance transcript exists yet.

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
are materialised by `ResultMaterializer` and covered by fixture tests (`s2-date-money-types`). Live `SqlDataReader`
behaviour against SQL Server for these types is not yet evidenced; `time`, `uniqueidentifier`, `sql_variant` etc.
remain unsupported (throw `NotSupportedException`).

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
