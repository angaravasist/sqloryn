# Delivery queue (active contract)

Authority: [DELIVERY-CONTRACT.md](DELIVERY-CONTRACT.md) · [MAC-EXPERIENCE.md](MAC-EXPERIENCE.md) · [CODEBASE-AUDIT.md](CODEBASE-AUDIT.md).
Evidence ledger: [CAP_EVIDENCE.csv](CAP_EVIDENCE.csv). PR #7 branch `codex/macos-database-studio`.

Counts are unequal statuses, not a percentage. Update this file when assigning or completing work.

## Status legend

| Status | Meaning |
|---|---|
| ready | No shared-file conflict; can start in a worktree |
| active | Worker assigned |
| blocked | Named external dependency prevents *this* task only |
| done-component | Code + component tests; native/live still open |
| done-native | Native/live evidence recorded |

## Workstream queue

| ID | Workstream | Task | Status | Blocker |
|---|---|---|---|---|
| WS4-T1 | Query fidelity | `time` + `uniqueidentifier` typed cells, CSV/JSON | done-component | Live CORE for reader |
| WS4-T2 | Query fidelity | Retained-row sort (+ Core filter API); Results header sort | done-component | Filter UI box still missing |
| WS5-T1 | Schema/edit | Transactional designer apply + freshness | done-component | Live SQL |
| WS5-T2 | Schema/edit | Edit Rows insert/delete | done-component | Live SQL |
| WS2-T1 | Explorer | FK/check/default/trigger/sequence/synonym + denial | done-component | Live expand |
| WS2-T2 | Explorer | Client filter + object overview | done-component | Native UI walk |
| WS12-U1 | Mac UI | Semantic theme + WelcomeView + toolbar | done-component | Screenshots (Screen Recording denied / RenderTimer in agent shell) |
| WS12-U2 | Mac UI | Settings groups with real prefs | done-component | — |
| WS12-P1 | Distribution | RIDs + provenance hashes (FD + SC scripts) | done-component | Clean Mac without .NET |
| WS3-T1 | Editor | AvaloniaEdit MIT; line numbers; T-SQL colour; go-to-line | done-component | Native typing/VoiceOver |
| WS1-T1 | Connections | Structured DB names; capability probe; auth gates; groups | done-component | Entra/Kerberos fixtures |
| WS6-T1 | Import | CSV preview/plan/apply + ImportCsvWindow | done-component | Live INSERT on disposable SQL |
| WS7-T1 | Security | Logins/users/roles read + GRANT/DENY/REVOKE preview + confirmed transactional apply (SecurityWindow) | done-component | Restricted principal fixture for live; role-change check from separate principal; login/role-membership writes not built |
| WS8-T1 | Operations | Backup/restore/CHECKDB plans + OperationsWindow (preview, confirm, honest outcome) | done-component | Disposable SQL Server fixture for live backup/restore/CHECKDB; VERIFYONLY/chain validation not built |
| WS9-T1 | Agent/plans | Safe plan XML parse + AgentWindow | done-component | Agent-enabled server for live |
| WS10-T1 | HA/repl | Capability-gated incomplete shells (`GatedWorkspaceCatalog`, Tools menu) | done-component (shell only; feature stays incomplete) | Multi-node topology |
| WS11-T1 | BI/cloud | Explicit unsupported gates (SSISDB/SSAS/SSRS/Fabric/Azure disabled) | done-component (shell only; feature stays incomplete) | Service protocol/auth |
| CAP-176 | AI | Deferred | blocked | Product deferral |

## Integrated tip (this wave)

Merged into `codex/macos-database-studio`: WS1–WS12 worktrees through ws11-gates.

Local integrated checks: Release 0/0, Core **217 PASS** (0 FAIL across 3 reruns), Python **51 OK**, preflight PASS, arm64 FD `.app` with provenance. Screenshots blocked.

## Next ready queue (reconciled 4 Nov 2026)

Stale duplicates removed: WS9 parser/AgentWindow, WS10/WS11 gate shells, and hosted six-scenario SQL already landed as done-component / live-partial.

1. **CSV fidelity trio** (below) — first priority.
2. Live acceptance for this wave is active/partial: the harness names the new scenarios; a disposable Linux run still has to pass them. Keep CORE-PLAN-DENIED required when the fixture can create the login (already pass=6 skip=0 on 0af9108).
3. WS4-T2b: Results filter UI box (Core filter API exists).
4. WS7 follow-up: login/role-membership writes; live restricted-principal + separate-principal verify.
5. WS8 follow-ups: RESTORE VERIFYONLY/FILELISTONLY, files/filegroups, index maintenance; live backup round trip.
6. WS9 follow-up: alerts/operators; Messages graphical plan operators (SafeShowPlanXml exists).
7. Missing CAP rows in scope (not HA/BI/Entra/multi-node) — assign owners in CAP_EVIDENCE.
8. Maintainer: Screen Recording for screenshots; clean-Mac SC install; signing keys.
9. HA/BI remain **incomplete features** (gated shells only).

## External gates (do not convert to “done”)

- Native Mac SQL fixtures and expanded Core scenarios for the new write workflows. The existing Linux CI fixture passed six Core scenarios on 0af9108.
- Screen Recording / VoiceOver for native a11y walk and MAC-EXPERIENCE screenshot set
- Agent shell Avalonia RenderTimer failure when launching without interactive display session
- Entra / Kerberos fixtures
- Multi-node HA/replication topology
- SSISDB/SSAS/SSRS/Fabric credentials and licences
- Maintainer signing/notarisation keys and publication
- Clean Mac without preinstalled .NET for SC package proof

## Independent review of 0af9108 on 4 November 2026

The hosted component and CodeQL checks passed on this commit. The normal PR run skipped SQL integration. The authorised disposable Linux workflow was dispatched as [run 41793052872](https://github.com/angaravasist/sqloryn/actions/runs/41793052872). The full workflow completed successfully: Linux, Mac arm64 and Mac Intel components, repository safeguards, and SQL integration. The SQL job passed all six Core scenarios (pass=6, skip=0, fail=0) and PROBE-10 cleanup. See STATE for scope.

Keep the full DELIVERY-CONTRACT active. The following are actionable corrections within it, not a replacement scope.

| Task | Acceptance | Status |
|---|---|---|
<<<<<<< HEAD
| CSV exact CREATE types | `ToSqlTypeText` keeps scale 0–38 (refuses out of range); preview guesses scale ≤38. Component tests: `csv-create-decimal-scale-exact`. Live exact stored-value check remains under CORE-CSV-IMPORT. | done-component |
| CSV existing target fidelity | `CsvDestinationType` + `CsvDestinationFidelity` on Append/Replace; ImportCsvWindow passes catalog MaxLength/Precision/Scale. Refuses lossy decimal/money/varchar/nvarchar/datetime before begin. Tests: `csv-destination-fidelity`. | done-component |
| CSV validated input identity | `CsvImportSource.LoadImmutable` snapshots + SHA-256; Apply re-hashes path before begin and refuses same-row-count content changes (Replace never deletes). UI uses LoadImmutable. Tests: `csv-immutable-source-fingerprint`. | done-component |
| Live acceptance for this wave | Expand Core scenarios for transactional schema failure/freshness, insert/delete conflicts, CSV rollback/fidelity/file changes, security denial and separate-principal effect, backup/restore round trip, time/GUID and Agent where available. Require expected cases explicitly. Harness expansion on `wt/core-wave` (ad04f99); live evidence still required on disposable Linux. | active |
=======
| CSV exact CREATE types | CsvSqlTypes.ToSqlTypeText currently clamps decimal scale to 18. Reject or preserve values requiring greater scale without silently changing them. Check full precision/scale and range before mutation, including values beyond the preview sample. Live test exact stored value from a second session. | ready |
| CSV existing target fidelity | Carry actual destination type/length/precision/scale into Append/Replace validation. Reject lossy conversions before deleting or inserting. Include decimal rounding, money, date/time and text widths. A successful SQL conversion is insufficient proof of faithful import. | ready |
| CSV validated input identity | CsvImportSource.FromFile permits concurrent writes and validation/apply open the file separately. RunAsync only compares row counts after insertion. Use a bounded-memory immutable snapshot or verify a content fingerprint before commit. Test changed content with unchanged row count, including Replace, and confirm old target data survives. | ready |
| Live acceptance for this wave | Expand Core scenarios for transactional schema failure/freshness, insert/delete conflicts, CSV rollback/fidelity/file changes, security denial and separate-principal effect, backup/restore round trip, time/GUID and Agent where available. Require expected cases explicitly. Harness now names CORE-TIME-GUID, CORE-DESIGN-APPLY, CORE-EDIT-INSERT-DELETE, CORE-CSV-IMPORT (required passes) plus CORE-SECURITY and CORE-BACKUP (pass or explicit skip). No new live pass is claimed until the disposable Linux job runs. | active/partial |
>>>>>>> wt/core-wave
| Queue reconciliation | Remove duplicate ready tasks that already landed, split done-component rows with missing UI into separate unfinished subactions, and assign all missing CAP rows within scope. HA/BI gate screens remain incomplete features. | ready |

The prior consent covers a disposable GitHub-hosted Linux SQL test with temporary credentials and cleanup. Keep that runner distinct from native Mac qualification and additional topology/service credentials. Verify current native automation availability rather than treating an earlier Screen Recording failure as universal.
