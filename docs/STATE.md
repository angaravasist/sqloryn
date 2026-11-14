# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-11-14. Experimental; no release support.

## Work under review

- Draft [PR #7](https://github.com/angaravasist/sqloryn/pull/7), branch `codex/macos-database-studio`: integrated studio preview ([SPEC-003](specs/003-macos-database-studio.md), [ADR-004](decisions/004-macos-database-studio.md)). It is the single integration PR for the [delivery contract](ssms/DELIVERY-CONTRACT.md) and [Mac experience](ssms/MAC-EXPERIENCE.md). Task status is in the [queue](ssms/DELIVERY-QUEUE.md) and [CAP evidence](ssms/CAP_EVIDENCE.csv).
- Dependencies: [PR #18](https://github.com/angaravasist/sqloryn/pull/18) (Avalonia 12.1.6, SqlClient 7.1.2, PKCS 10.0.15, setup-dotnet 6.1.0) is merged to `main` and merged into this branch.

## Evidence

| Claim | Status |
|---|---|
| Release build (0 warnings), Core / probe / Python tests, preflight, framework-dependent `.app` | Hosted `probe-components` on Linux, macOS 15 arm64 and macOS 15 Intel on every push. Results are on the PR checks. |
| Live Linux SQL acceptance: 12 Core scenarios plus PROBE-10 cleanup | Run on the PR head with `workflow_dispatch`. The run link and exact head SHA are in the PR #7 description, because a docs commit cannot record a run on itself. Previous run: `496b503`, [run 41807915637](https://github.com/angaravasist/sqloryn/actions/runs/41807915637). |
| Recovery writer and privacy settings | Component-tested (`recovery-writer`): rapid edits, save, discard, quit flush, multiple tabs, disable, retention |
| Native Mac SQL (arm64 or Intel) | **Not accepted** on this branch. The PR #6 arm64 `SELECT 1` result is history only. |
| Native UI walk, VoiceOver, screenshots | **Not accepted** |
| Windows SSH product path | Closed, not recreated |

### Live Linux scenarios

| Scenario | Asserts |
|---|---|
| CORE-MATERIALISE | date/money/smallmoney, NULL, negative, fractional and extreme values |
| CORE-EXPORT | Materialised values preserved in CSV and JSON |
| CORE-DESIGN-PK | CREATE TABLE and composite PK, observed independently |
| CORE-EDIT-CONFLICT | Case-only conflict refused; fresh update committed; batch rollback |
| CORE-PLAN-INSERT | Estimated INSERT returns a plan and inserts no row |
| CORE-PLAN-DENIED | Restricted-principal estimate denied; no row inserted |
| CORE-TIME-GUID | time and uniqueidentifier, read from a second session |
| CORE-DESIGN-APPLY | Create/add, failed-batch rollback, stale fingerprint refused |
| CORE-EDIT-INSERT-DELETE | Insert/delete conflict rolls back; fresh delete commits |
| CORE-CSV-IMPORT | Create/Append exact values beyond the preview sample; fidelity/fingerprint refusals |
| CORE-SECURITY | GRANT preview changes no permissions (apply is not called) |
| CORE-BACKUP | COPY_ONLY backup recorded in msdb (no restore assertion) |

A component pass or a green `SqlClientProbe` is not live evidence. Linux evidence does not qualify native Mac SQL or UI.

## Open gaps

- Live coverage still missing: security apply and restricted-principal effects, restore round trip, Agent, hostile-DDL apply, `MAX` values and collation edge cases.
- Native Mac acceptance: SQL from arm64 and Intel apps, an Intel walkthrough, VoiceOver, screenshots and layout sizes. Screen Recording permission decides whether screenshots are possible on the review Mac.
- Distribution: self-contained runtime licence review, signing and notarisation (maintainer decisions).
- Earlier review findings 1–18 remain **Partial**: code and component tests exist, and the remaining native/live parts are listed above. The detailed history is in git and [CODEBASE-AUDIT](ssms/CODEBASE-AUDIT.md).

## Local data on the Mac

Everything lives under `~/Library/Application Support/SQLoryn`. See the [threat model](THREAT_MODEL.md#local-app-data) for the full list. Unsaved-tab copies and execution history are on by default with 30-day retention. Settings › Privacy turns them off, changes retention, and clears them.

## Reviewer launch

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore -c Release
dotnet run --project tests/Sqloryn.Core.Tests -c Release --no-build
bash scripts/build_macos_app.sh
open artifacts/macos/osx-arm64/SQLoryn.app
```

## Explicit non-claims

Not SSMS-compatible. Not signed or notarised. AI assistant deferred. No Windows SSH product path. HA, replication and BI/cloud windows are explanations only.
