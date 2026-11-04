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

## Workstream queue (initial dispatch)

| ID | Workstream | Task | Owner files | Status | Blocker |
|---|---|---|---|---|---|
| WS4-T1 | Query fidelity | `time` + `uniqueidentifier` typed cells, CSV/JSON, Core acceptance | `ResultMaterializer`, `ResultExport`, SqlAcceptance | ready | Live SQL for CORE |
| WS4-T2 | Query fidelity | Retained-row grid sort/filter + NULL/type labels in Results UI | `MainWindow.Results`, ResultSetView | ready | — |
| WS5-T1 | Schema/edit | Transactional ALTER when SQL Server allows; freshness check; independent verify path | `TableDesign*`, TableDesignWindow | component-done (fakes); live SQL pending | Live SQL |
| WS5-T2 | Schema/edit | Safe insert/delete Edit Rows with confirm + conflict | `EditRows*` | component-done (fakes); live SQL pending | Live SQL |
| WS2-T1 | Explorer | FK/check/default/trigger/sequence/synonym folders + honest denial | `CatalogClient`, SchemaObjectCatalog, MainWindow OE | ready | Live expand |
| WS2-T2 | Explorer | Search/filter + object overview workspace pane | App OE UI | ready | — |
| WS12-U1 | Mac UI | Semantic theme resources; welcome/connected density; toolbar grouping | `App.axaml`, MainWindow.Shell | ready | Screenshots |
| WS12-U2 | Mac UI | Settings groups backed by real prefs; recovery messaging | SettingsWindow | ready | — |
| WS12-P1 | Distribution | RuntimeIdentifiers + locked SC publish arm64/x64 + provenance hashes | csproj, build_macos_app.sh | ready | Clean Mac without .NET |
| WS3-T1 | Editor | Licence-reviewed editor component ADR; syntax + line numbers offline | App editor, NuGet | ready | Package licence OK |
| WS1-T1 | Connections | Database identifiers via structured APIs; capability probe stub | ConnectionOptions | ready | Entra fixture |
| WS6-T1 | Import | Flat-file import preview/mapping Core model (no write on preview) | `CsvImport*`, `ImportCsvWindow` | component-done (fakes); live SQL pending | Live INSERT/CREATE/DELETE on a disposable instance |
| WS7-T1 | Security | Logins/users list + GRANT preview Core (no apply without confirm) | new Security* | ready | Restricted principal fixture |
| WS8-T1 | Operations | Backup/restore guided UI shell + Core preview models | new Ops* | ready | Disposable DB fixture |
| WS9-T1 | Agent/plans | Plan XML safe parse + Messages graphical stub; Agent job list model | EstimatedPlan*, new Agent* | ready | Agent-enabled server |
| WS10-T1 | HA/repl | Capability-gated UI shells; incomplete until multi-node | docs + shells | ready | Multi-node topology |
| WS11-T1 | BI/cloud | Explicit unsupported/gated states for SSIS/SSAS/SSRS/Fabric | UI gates | ready | Service auth |
| CAP-176 | AI | Deferred | — | blocked | Product deferral |

## Active writers

| Worktree | Branch | Tasks |
|---|---|---|
| `.worktrees/ws4-types` | `wt/ws4-types` | WS4-T1, WS4-T2 |
| `.worktrees/ws5-schema` | `wt/ws5-schema` | WS5-T1, WS5-T2 |
| `.worktrees/ws2-explorer` | `wt/ws2-explorer` | WS2-T1, WS2-T2 |
| `.worktrees/ws12-mac` | `wt/ws12-mac` | WS12-U1, WS12-U2, WS12-P1 |

## Next after current wave

1. Integrate WS4/WS5/WS2/WS12 results; run Core + package.
2. Dispatch WS3 editor + WS6 import + WS1 connection polish.
3. Enable hosted sql-integration within existing licence consent; require CORE-PLAN-DENIED when fixture can create login.
4. Screenshots from packaged build; sqloryn-verifier; CAP_EVIDENCE row updates.

## External gates (do not convert to “done”)

- Disposable SQL Server (Linux CI / local Docker) for CORE-* and live journeys
- Screen Recording / VoiceOver for native a11y walk
- Entra / Kerberos fixtures
- Multi-node HA/replication topology
- SSISDB/SSAS/SSRS/Fabric credentials and licences
- Maintainer signing/notarisation keys and publication
- Clean Mac without preinstalled .NET for SC package proof
