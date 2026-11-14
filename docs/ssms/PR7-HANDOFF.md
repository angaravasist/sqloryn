# PR #7 maintainer handoff

Prepared 4 November 2026. Snapshot: `573d749e4c30ef88270b481cafcbdb4fe9b9a198`.
This is a takeover document, not a claim of product completion or a new scope.

## 1. Repository and delivery status

- Repository: `angaravasist/sqloryn`.
- [PR #7](https://github.com/angaravasist/sqloryn/pull/7): OPEN, DRAFT; base `main`; branch `codex/macos-database-studio`.
- Checkout: `/Users/vasistangara/Other/sqloryn`. Clean at the snapshot before these handoff files were added.
- Existing app location: `/Users/vasistangara/Other/sqloryn/artifacts/macos/osx-arm64/SQLoryn.app`. Rebuild before attributing the package to the current source. Existing package provenance was not independently rechecked for this handoff.
- Current product is an experimental, unsigned development application. It is not production-qualified, not a complete SSMS replacement and not a supported release.
- This handoff changes documentation only. No application changes, commits, pushes, merges, releases or network-exposure changes were made for it.
- Numerous historical writer worktrees remain under `.worktrees/`. Inspect `git worktree list` and each worktree's status before reuse/deletion. Their existence is not evidence of active work, and their individual cleanliness was not checked here.

Earlier conversational percentages were estimates without a defensible weighted denominator. Do not use them for planning or acceptance. Use the capability snapshot, workflow tests and explicit remaining work below.

## 2. What was commissioned for this PR

The active assignment is [DELIVERY-CONTRACT.md](DELIVERY-CONTRACT.md), paired with [MAC-EXPERIENCE.md](MAC-EXPERIENCE.md). It supersedes earlier incremental correction prompts. The objective is an installable macOS SQL Server studio with real SSMS-familiar development and administration workflows, a coherent Mac interface, safe failure behaviour and repository-contained evidence.

The contract keeps all 192 CAP identifiers and 420 SURF reference candidates accounted for. The reference catalogue is not a support claim. A capability must identify actual entry points, implementation, tests, target/platform and outstanding subactions. Externally blocked features remain incomplete; they are not silently removed to increase completion figures.

The twelve delivery families are connections; Explorer/scripting; editor/documents; execution/results; schema/data editing; import/transfer; security; operations; Agent/observability; HA/replication; BI/cloud; and Mac experience/distribution. The assignment is broader than the functionality currently implemented. There is no recorded maintainer acceptance of a reduced “query editor only” scope.

Optional AI assistance (CAP-176) remains explicitly deferred. Notebook and update-channel decisions remain unresolved as documented. Paid infrastructure, new credentials and signing/publication are not delegated to agents.

## 3. Architecture and where to work

Retain the existing single-process Avalonia App → Sqloryn.Core → Microsoft.Data.SqlClient architecture unless a justified ADR changes it. No product SSH bridge is required; SSH scripts are qualification tooling only.

| Area | Main implementation locations |
|---|---|
| Shell, command routing, tab ownership | `src/Sqloryn.App/MainWindow.*`, `QueryTab.cs`, `AppServices.cs` |
| Connections and secrets | `ConnectWindow.*`, Core profiles/settings/Keychain, `ConnectionCapabilities.cs` |
| Session and execution | `SqlSession.cs`, `IDatabaseSession.cs`, `BatchObserver.cs`, `GoBatchSplitter.cs`, `GoBatchExecution.cs`, outcome/timer models |
| Explorer and properties | `ExplorerController.cs`, `CatalogClient.cs`, Core metadata/catalog services, `ObjectProperties.cs`, `UserDefinedTypeCatalog.cs` |
| Editor, documents, recovery | AvaloniaEdit integration in `EditorSupport.cs`, `ScriptDocument.cs`, `SafeFileWriter.cs`, `DocumentRecovery.cs`, `ExecutionHistory.cs`, `MainWindow.Recovery.cs` |
| Results and export | `ResultMaterializer.cs`, `RawRowReader.cs`, `ResultPresentation.cs`, `ResultSetViewOps.cs`, exporters, `StructuredValueView.cs`, `MainWindow.Results.cs` |
| Schema and edits | `TableDesign*`, `EditRows*`, their App windows and SqlSession entry points |
| CSV import | `CsvImport*`, `CsvDestinationFidelity.cs`, `CsvValue.cs`, `ImportCsvWindow.*` |
| Permissions | `SecurityCatalog.cs`, `PermissionChange.cs`, `SecurityWindow.*` |
| Backup/restore/integrity | `OperationsPlans.cs`, `OperationsWindow.*` |
| Agent and plans | `AgentCatalog.cs`, `AgentSessionRunner.cs`, `AgentWindow.*`, `SafeShowPlanXml.cs`, session estimate path |
| Deferred service UI | `GatedWorkspace.cs`, `GatedWorkspaceWindow.cs`; these are explanations, not service implementations |
| Tests | `tests/Sqloryn.Core.Tests`, `tests/Sqloryn.Core.SqlAcceptance`, `tests/SqlClientProbe.Tests`, Python runner tests |
| Packaging | `scripts/build_macos_app.sh`, `assets/icon`, package evidence files |

SDK is pinned by `global.json` to 10.0.403 with latest-patch roll-forward. Read [dependencies](../DEPENDENCIES.md), lockfiles and ADRs for exact package versions/licences. AvaloniaEdit has been adopted; a framework rewrite is not part of this takeover.

## 4. Implemented behaviour and its limits

This table combines the current source/documentation and recorded tests. “Present” does not mean every native interaction or SQL target is accepted.

| Family | Present in the branch | Remaining limits |
|---|---|---|
| Connections | SQL authentication, mandatory encryption, explicit TLS trust/pin choices, profiles, optional endpoint-bound Keychain password storage, recents, multiple tab sessions, database switching, connect timeout and query settings | Entra/Kerberos absent; profile grouping UI, capability integration, diagnostics and other catalogue actions remain partial/ready |
| Explorer | Lazy bounded metadata, schemas and common objects, keys/constraints/triggers/sequences/synonyms, user-defined types, loaded-label filter, subtree refresh, overview/properties, SELECT scripting into a new tab | Search of unloaded objects, complete definitions/index detail, full-text/security/management navigation and broader scripting remain |
| Editor/documents | AvaloniaEdit colouring/line numbers, tabs, file lifecycle, find/replace, go-to-line, GO preflight, selected/document/current-batch execution, query options, recovery and history | IntelliSense, diagnostics annotations, formatting, folding/snippets and further project workflows incomplete; SQLCMD unsupported directives are rejected, not implemented |
| Execution/results | Bounded typed results, multiple sets, messages, cancellation/timeout classification, target/transaction status, grids, retained-row sort/filter, CSV/JSON export, binary rendering, retained XML/JSON viewing and elapsed status | Full type matrix, streaming output and other result actions remain; native responsiveness and new UI interactions need acceptance |
| Schema/data | New table with PK, transactional designer apply with freshness checks, edit/update/insert/delete paths, conflicts and rollback | Full object designers, constraints/dependencies, schema compare, diagrams, package workflows and many catalogue operations missing |
| Import | Delimited-file preview, mapping, Create/Append/Replace, validation, exact decimal scale, destination fidelity checks, immutable source/fingerprint handling | Native import journey remains open; more data transfer/package workflows are missing |
| Security | Principal/role catalogue, membership reads, permission preview and confirmed transactional apply code | Live test covers preview purity only; permission apply/denial/effective rights, login/user/role mutation workflows need further implementation or verification |
| Operations | Backup/restore/CHECKDB plan/preview/confirmation code | Backup live test is narrow; restore round trip, VERIFYONLY/chain, files/filegroups, server settings and wider maintenance remain |
| Agent/diagnostics | Agent jobs/schedules/history reads with gated run/delete; estimated plan path and safe XML handling | Agent live evidence, job editing/alerts/operators, graphical/actual plans, Query Store/XE and wider observability remain |
| HA/replication/BI/cloud | Discoverable explanation windows | Actual service functionality is not implemented; no health, failover, replication or BI execution claim |
| Mac/distribution | Semantic theme, welcome/settings, native-menu/Command integration, original icon, RID packaging and provenance mechanism | Premium UI acceptance, VoiceOver, resize/performance, current native SQL, clean-Mac self-contained install and signed distribution are outstanding |

Historical PR #6 evidence included an Apple Silicon desktop connection, system database navigation and `SELECT 1`, with lab cleanup. That is useful history, not qualification of PR #7's current workflows.

## 5. Exact inventory snapshot and discrepancies

[PR7-HANDOFF-INVENTORY.csv](PR7-HANDOFF-INVENTORY.csv) contains every CAP exactly once, including the CSV capability represented separately by integrator rows in DELIVERY-QUEUE. It preserves task descriptions and blockers at this SHA. It is a frozen handoff snapshot; continue maintaining DELIVERY-QUEUE and CAP_EVIDENCE as the active records.

Actual parsed inventory counts:

| Status | Distinct CAPs |
|---|---:|
| done-component | 86 |
| partial | 22 |
| ready | 61 |
| blocked | 12 |
| gated-incomplete | 10 |
| alias | 1 |
| Total | 192 |

There are 85 done-component CAP rows in the queue's main table plus CAP-122 represented by three CSV integrator rows. LIVE-WAVE is a test task, not another capability. The queue prose says 89 done-component inventory rows, which does not match its table. Earlier conversational “90 implemented” inherited this accounting problem. Do not count integrator tasks as additional capabilities.

“done-component” is the queue's label, not a complete feature audit: it may describe a narrow implemented subaction. For example rejecting SQLCMD directives does not deliver SQLCMD execution. Inspect the full corresponding SURF actions before closing a family.

## 6. Test evidence verified for this handoff

| Evidence | Revision / result | What it proves |
|---|---|---|
| [Current components](https://github.com/angaravasist/sqloryn/actions/runs/41817474500) | Current PR tip; Linux, Mac arm64, Mac Intel and safeguards PASS; SQL integration SKIPPED | Component/build/package checks; not native interactive or SQL acceptance |
| [Current CodeQL](https://github.com/angaravasist/sqloryn/actions/runs/41817467490) | Current PR checks PASS for C#, Python and Actions | Static analysis; not a proof of security or correctness |
| [Live Linux SQL](https://github.com/angaravasist/sqloryn/actions/runs/41807915637) | `496b5039dab16b7f8c5bf8abd7d81e3d54bcc70e`; 12 PASS, 0 SKIP, 0 FAIL; cleanup PASS | Named scenarios below on SQL Server 16.0.4305.5 Developer, disposable Linux x64 |
| Cursor local report | `573d749`: Release 0 warnings/errors, Core 246 PASS | Reported by implementer; not independently rerun locally for this handoff |
| Independent reviews reported by Cursor | Intermediate `496b503` / `1a1d515` | Do not cover every later integrated change |

Live scenarios:

1. CORE-MATERIALISE: date/money/smallmoney, null/negative/fractional/extreme values.
2. CORE-EXPORT: materialised CSV/JSON fidelity.
3. CORE-DESIGN-PK: CREATE TABLE/composite PK, independently observed.
4. CORE-EDIT-CONFLICT: case-only conflicts, fresh update and batch rollback.
5. CORE-PLAN-INSERT: estimate does not insert a row.
6. CORE-PLAN-DENIED: denied estimate does not insert a row.
7. CORE-TIME-GUID: time/uniqueidentifier materialisation.
8. CORE-DESIGN-APPLY: create/add, failed-batch rollback, stale fingerprint refusal.
9. CORE-EDIT-INSERT-DELETE: conflict rollback and fresh delete.
10. CORE-CSV-IMPORT: exact Create/Append values beyond the preview sample and refusal checks.
11. CORE-SECURITY: GRANT preview changes no permissions; Apply intentionally not called.
12. CORE-BACKUP: COPY_ONLY backup recorded in msdb. No restore/data recovery assertion.

PROBE-10 verified disposable container/network/temp cleanup. The live test is on an ancestor: 45 files changed between it and the snapshot, including session, result and recovery paths. The test runner currently allows some named scenario skips; require expected passes on a controlled fixture and never treat an allowed skip as qualification.

## 7. Review findings and documentation debt to carry forward

1. **Recovery responsiveness** (fixed 14 Nov 2026: `RecoveryWriter`, debounced/serialised off the UI thread; `recovery-writer` tests): `OnEditorTextChanged` directly invokes `PersistRecovery`; that reads and serializes dirty document copies and calls an atomic writer with disk flush on the UI thread. Correct using bounded, serialized, debounced persistence off the UI thread, with immutable snapshots and ordering so older writes cannot restore discarded/saved content. Test rapid edits, delayed writes, save/discard/quit and multiple tabs. Code path inspected; actual typing latency was not measured.
2. **Recovery/history privacy** (fixed 14 Nov 2026: opt-in switches, retention and Clear in Settings › Privacy; storage listed in THREAT_MODEL): the contract requires opt-in storage rules. Current recovery path writes automatically and uses diagnostic redaction, which is not a guarantee that arbitrary confidential SQL literals are removed. Verify and implement discoverable consent, clear/disable behaviour and retention policy. Keep secret handling separate from convenience history. This is a contract gap to resolve, not evidence of a network disclosure.
3. **Security live coverage:** expand beyond pure preview to actual apply, restricted-principal effects, GRANT/DENY/REVOKE and failure behaviour.
4. **Restore live coverage:** add VERIFYONLY and isolated restore with independent schema/data comparison, invalid input and cleanup. A backupset record is insufficient.
5. **Final integrated review:** review changes after the last independently reviewed revision and rerun relevant live tests on the implementation being proposed for acceptance.
6. **Stale evidence statements** (fixed 14 Nov 2026 in FEATURE_MATRIX and STATE): FEATURE_MATRIX still describes several live-tested scenarios as component-only and refers to the older six-scenario run. STATE's historical dispositions also say live SHOWPLAN/date/money/edit evidence is absent despite later evidence above. Preserve history but make current dispositions accurate.
7. **Queue count mismatch** (fixed 14 Nov 2026: 85 table rows, 86 distinct CAPs): correct the prose count described in section 5. Do not equate presence of a screen with delivery of its reference capability.
8. **Native limitations are specific:** prior report says AX trusted but Screen Recording denied. Recheck current process permissions. Screenshot denial does not prevent all keyboard/accessibility interaction; it does not by itself block SQL acceptance. VoiceOver testing and recording are also distinct activities.

The previous CSV trio is no longer an outstanding defect merely because it appears in older review prompts: exact scale, destination checks and immutable/fingerprint work landed and obtained the stated live evidence. Regressions and uncovered cases still require normal review.

## 8. Remaining scope and next owner's execution plan

The exact 61 ready and 22 partial rows are in the attached inventory; the descriptions preserve all IDs. Broad remaining work includes connection-string/discovery/grouping and capability UI; unloaded-object search and definitions; completion/formatting/snippets; transaction controls and result actions; full schema/object design and compare; security administration; restore/maintenance/configuration; Agent/monitoring; source/project integration; and multi-window/sleep/crash behaviour.

Use the existing full contract, not a new sequence of tiny feature PRs. Suggested takeover order within PR #7:

1. Checkpoint this handoff, reconcile current CAP/SURF status and assign owners to ready work. Preserve local changes. Reserve common shell/routing, package locks, CI and evidence for one integrator.
2. Fix the concrete recovery/acceptance gaps above while parallel writers complete independent ready workflows in isolated worktrees. Integrate reviewed commits and immediately assign the next ready task.
3. For each workflow finish its discoverable UI, Core path, loading/empty/denied/error/cancel states, tests and documentation together. Do not close an action because a helper or disabled button exists.
4. Exercise real generated mutations on disposable SQL, verify effects from independent sessions/principals, and test cancellation/rollback/unknown outcomes. Never replay an uncertain write automatically.
5. Complete the Mac design/interaction acceptance alongside implementation, rather than postponing all UI work to final QA.
6. Independently review the final integrated implementation, qualify packages and report exact residual external gates. A checkpoint due to execution limits must retain the next tasks; it is not product completion.

This is not a promise that the entire comparator can be delivered without further feasibility work. Any unsupported library/protocol/platform combination needs an explicit decision and evidence; no silent removal of scope or fabricated substitute workflow.

## 9. Native UI and release acceptance still required

The Mac experience contract calls for a coherent toolbar/target selector, navigable Explorer, compact document area, optional inspector, resizable output/activity and useful welcome/connection/settings/recovery screens. Use system theme/accent and semantic resources, original assets and consistent command availability.

Verify actual packaged build provenance and operate populated workflows with synthetic data. Capture welcome, connection validation, Explorer, query/results, structure/edit review, plan, operation, Settings and recoverable error in light/dark/system appearance. Test 1280×800 and 1024×700 logical layouts, text/contrast scaling, keyboard-only paths, Command menus, Escape, focus return and VoiceOver. Measure editor/input readiness and grid scrolling, not only process-start time.

Self-contained arm64/x64 packaging, runtime redistribution review, notices/SBOM/hashes, a Mac without preinstalled .NET, signing/notarisation and release approval remain separate requirements. An existing icon and `.app` folder do not close distribution acceptance.

## 10. Authorisations and boundaries

- Existing approval covers the disposable GitHub-hosted Linux x64 SQL Server Developer test, temporary generated credentials, synthetic data and verified cleanup. Use the existing licence gate. No need to reopen Windows or seek that same approval again.
- Windows SSH forwarding/firewall/key connectivity was deliberately removed. Do not recreate it, reuse historical host details or interpret old lab commands as current permission.
- Native Mac SQL testing needs an available authorised disposable fixture. Linux acceptance does not substitute for native arm64/x64 evidence.
- Entra/Kerberos, Azure/Fabric/BI services, multi-node HA/replication and Agent configuration may need specific fixtures, protocols, licences or credentials. Separate implementation gaps from fixture-only blockers and continue unrelated work.
- Do not provision paid resources, make credential decisions, access production, publish, merge or weaken protections without maintainer authorisation.
- All input/metadata/files are untrusted. Preview and confirm destructive operations; preserve exact types, target ownership, TLS validation and uncertainty. Keep secrets and confidential data out of repository/logs/CI.

## 11. Reproduction commands

From the repository root (SDK/runtime as pinned):

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore --configuration Release
dotnet run --project tests/SqlClientProbe.Tests --no-build --configuration Release
dotnet run --project tests/Sqloryn.Core.Tests --no-build --configuration Release
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 scripts/repo_preflight.py
git diff --check
bash scripts/build_macos_app.sh
open artifacts/macos/osx-arm64/SQLoryn.app
```

Use `osx-x64` for an Intel package. Refer to TESTING for self-contained flags/provenance and exact supported runner requirements.

Existing authorised hosted SQL invocation, after reviewing the intended branch revision:

```sh
gh workflow run repository-safeguards.yml \
  --repo angaravasist/sqloryn \
  --ref codex/macos-database-studio \
  -f accept_sql_server_license=true
```

Record the resulting run's full head SHA, required CORE pass names, skips/failures and cleanup. A no-environment Core executable can exit successfully with SKIP; that is not SQL evidence. Do not run the Linux Docker runner directly on Apple Silicon expecting equivalent qualification.

## 12. Completion and handover documents

Before closing the product assignment: all supported-scope CAP/SURF subactions are implemented and accepted; every external gate remains explicit; no unreviewed integrated safety changes remain; real SQL and native UI acceptance cover claimed workflows; package/runtime/licence evidence exists; and the maintainer approves release-related actions. PR merge and product release are distinct decisions. Current recommendation remains draft/changes required.

Read in order: repository AGENTS/README/SECURITY → this handoff → DELIVERY-CONTRACT → MAC-EXPERIENCE → handoff inventory and active DELIVERY-QUEUE/CAP_EVIDENCE → STATE/TESTING → architecture/ADRs/dependencies/threat model → source/tests. CODEBASE-AUDIT is a dated baseline, not a fresh audit of every later commit.

This handoff verified branch/PR status, current hosted check results and inventory counts, and carries forward the specifically inspected live logs/source findings. It did not rerun local suites, launch a fresh package, perform a full new security audit or qualify native UI. No claim of a validated overall completion percentage is made.
