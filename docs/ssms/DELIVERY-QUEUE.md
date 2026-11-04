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
| WS7-T1 | Security | Logins/users + GRANT preview | ready | Restricted principal fixture for live |
| WS8-T1 | Operations | Backup/restore guided shell + Core models | ready | Disposable DB fixture |
| WS9-T1 | Agent/plans | Safe plan XML parse + Agent job list model | ready | Agent-enabled server |
| WS10-T1 | HA/repl | Capability-gated incomplete shells | ready | Multi-node topology |
| WS11-T1 | BI/cloud | Explicit unsupported gates | ready | Service auth |
| CAP-176 | AI | Deferred | blocked | Product deferral |

## Integrated tip (this wave)

Merged into `codex/macos-database-studio`: ws4-types, ws5-schema, ws2-explorer, ws12-mac, ws1-connect, ws3-editor, ws6-import.

Local integrated checks (SHA recorded in STATE after commit): Release 0/0, Core **169 PASS** named lines + summary (0 FAIL), Python **51 OK**, preflight PASS, arm64 FD `.app` with provenance.

## Next ready queue

1. WS7 security admin Core + list UI (preview-only apply path).
2. WS8 backup/restore models + guided window (no live restore without fixture).
3. WS9 plan XML safe parser + Agent job catalogue read model; wire Messages plan viewer stub.
4. WS10/WS11 gated incomplete screens naming missing topology/service.
5. Hosted `sql-integration` within existing licence consent; require CORE-PLAN-DENIED when login creation works.
6. Maintainer: Screen Recording for screenshots; clean-Mac SC install; signing keys.

## External gates (do not convert to “done”)

- Disposable SQL Server (Linux CI / local Docker) for CORE-* and live journeys
- Screen Recording / VoiceOver for native a11y walk and MAC-EXPERIENCE screenshot set
- Agent shell Avalonia RenderTimer failure when launching without interactive display session
- Entra / Kerberos fixtures
- Multi-node HA/replication topology
- SSISDB/SSAS/SSRS/Fabric credentials and licences
- Maintainer signing/notarisation keys and publication
- Clean Mac without preinstalled .NET for SC package proof
