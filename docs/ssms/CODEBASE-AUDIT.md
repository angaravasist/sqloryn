# Implementation audit, 4 November 2026

Baseline source is commit 06bc2ad5cb88a3586368c3ccb321c0ce922649b3 on draft PR 7. This audit reads application command wiring, Core services, SQL generators, tests, packaging, CI and the existing catalogue. It supplements the earlier eighteen findings. It establishes code presence and identified gaps. Full runtime and target coverage still requires the acceptance work below.

## Independently executed

GitHub checks on 06bc2ad completed successfully for Ubuntu, both Mac component jobs, repository safeguards and CodeQL. Live SQL integration was skipped. [Component run](https://github.com/angaravasist/sqloryn/actions/runs/41750374026) · [CodeQL run](https://github.com/angaravasist/sqloryn/actions/runs/41750368912)

- Release build using the pinned local SDK, with no restore and build servers disabled, passed with zero warnings and errors.
- Core executable passed 109 named assertions plus its summary. Counting the summary as a test yields the misleading number 110.
- Provider executable passed 35 assertions.
- Python discovery passed 51 tests. The initial sandboxed run failed the loopback-port test because binding was prohibited. The permitted rerun passed.
- Repository preflight and whitespace checks passed.
- The existing packaged arm64 app opened. Its native menu identified SQLoryn. The welcome action focused the editor. With synthetic SQL, Command-F opened Find and Replace. Command-W opened the dirty prompt. Cancel preserved the text and dirty indicator. The audit's temporary text was then discarded through the prompt.
- Screenshot and accessibility-tree access worked in this Codex session. This does not establish Cursor's permissions or a VoiceOver walk. It demonstrates that offline native checks can be performed with an available UI tool.
- The existing packaged Sqloryn.dll hash differs from the freshly built Release DLL. The UI observations describe that local package, whose exact source provenance was not established. Future packaging must record its source commit and output hashes.
- No SQL instance was contacted. No live Core acceptance, Entra, BI, HA, replication, clean-machine installation or signed distribution was verified.

## Implemented and pending by product area

Paths are relative to the repository root. A named implementation can cover only part of a capability.

| Area | Present in application/Core | Required expansion |
|---|---|---|
| Connections | ConnectWindow, ConnectionOptions, ConnectionProfileStore, ProfileAutofillSession, ConnectAutofillController, SecretStore. SQL authentication, TLS, optional pin, profile persistence, optional Keychain, per-tab sessions. | Capability discovery, server groups, broader supported identity methods and endpoint validation. ConnectionOptions requires UserId/Password and excludes brackets in database names. Preserve legitimate SQL names through structured connection-string APIs. Verify actual Keychain events and separate target state. |
| Explorer | CatalogClient, MetadataCatalog, SchemaObjectCatalog and MainWindow expansion. Database/schema lists, tables/views/procedures/functions, key/index listings, column details and capped output. | Search/filter, subtree refresh, definitions, richer properties, FK/check/default/trigger/sequence/synonym/type/full-text/security/management nodes. Current procedure/function/key/index nodes terminate expansion. Metadata denial needs a useful state. |
| Editor | MainWindow.axaml uses a multiline TextBox. ScriptDocument, SafeFileWriter, FileFingerprint and FindReplaceEngine implement file lifecycle and find/replace. | SQL highlighting, line numbers, go-to-line, diagnostics navigation, completion, folding, formatting, snippets, recovery, history and project association. No T-SQL editor engine is installed. Ordinary text entry must remain offline. |
| Execution | SqlSession, GoBatchSplitter, BatchObserver, BatchAggregate, QueryOutcomeFormatter and RunGuard. Selection/document execution, sequential GO batches, cancellation/timeout and uncertain-result handling. | Visible transaction state, database selector, fuller SQL options, exact server-error mapping and reconnect/restart journeys. SQLCMD directives currently fail preflight. SQLCMD execution needs explicit file/command boundaries. |
| Results | ResultMaterializer, RawRowReader, ResultExport and MainWindow.Results. Per-set DataGrid, messages, CSV/JSON, decimal precision, date/money and limits. | time, uniqueidentifier, sql_variant and supported version-specific types. Production typed formatting still rejects unhandled types. Grid columns explicitly disable sort. Add filter/sort with retained-row scope, value inspection, results-to-text and streaming results-to-file with completeness semantics. |
| Schema/design | TableDesign, TableDesignWindow and ScriptFromObjectPlan. CREATE TABLE with optional PK, column changes, script SELECT into a new targeted tab. | Full table/key/index/constraint definitions, computed/default expressions, object CREATE/ALTER/DROP scripting, dependencies, object editors, diagrams and schema compare/deployment. Existing-table PK changes are refused. |
| Data editing | EditRowsPolicy, EditRowsSql, EditRowsBatch and EditRowsWindow. Keyed updates, optimistic checks, owned transaction, restrictions and omitted-cell handling. | Live conflict/trigger/rollback qualification, insert/delete where safe, clearer unsupported values and refresh after uncertain effects. Safe generated updates are not evidence for arbitrary edits. |
| Import and deployment | CSV/JSON export exists. | Flat-file import, preview/mapping/errors/transactions, data transfer, DACPAC/BACPAC and SQL projects. Package support requires concrete upstream evaluation and licences. |
| Security administration | Keychain protects client credentials. | Logins, users, roles, mappings, permissions, ownership, audit, credentials and proxies. Client password storage does not implement server administration. |
| Operations | DiagnosticsWindow exports a sanitised support bundle. | Database/server configuration, backup/restore, files/filegroups, integrity checks, index maintenance, logs, mail, linked servers and policy surfaces. A generic query tab does not satisfy these GUI workflows. |
| Agent and observability | EstimatedPlanService and SqlPlanCommandRunner provide bounded estimated XML in Messages. | Agent job/step/schedule/history/alert/operator UI, actual/graphical plans, Query Store, XE, activity/blocking, statistics and reports. Normal query execution must never be repeated implicitly to obtain diagnostics. |
| HA and replication | No dedicated command/view/provider implementation found. | Target-qualified topology, health, configuration, monitor and operation flows, including data-loss boundaries and multi-node tests. |
| BI and cloud | SQL auth can address a network server. | SSISDB/SSIS, SSAS connection and XMLA/MDX/DAX, supported SSRS tasks, migration, Fabric and Azure-specific operations. Protocol/service availability and authorisation are separate requirements. |
| Mac and distribution | App, AppServices, MainWindow.Shell, SettingsWindow, original icon, native menu, system theme, welcome and .app script. | Real keyboard/VoiceOver coverage, ready-for-input timing, sleep/wake, crash recovery, multi-window, reproducible self-contained packaging, notices, clean-machine install and approved signing/notarisation. |

The catalogue currently has 47 SPEC-003 assessments, 140 unassessed candidates, three transport-only assessments, one alias and one deferred AI entry. These are documentation classifications rather than implementation percentages. CAP-073/074/094 need particular reconciliation because InfoMessage handling, result errors and Unicode text paths exist in current code while the rows still describe transport-only work. Several desktop entries also remain unassessed despite shell code. Audit by entry point, output and test before changing a row.

## Specific gaps to carry into implementation

1. **Common data types still incomplete.** ResultMaterializer.FormatSqlTypedCell has no time or uniqueidentifier path. A row containing an unhandled type fails materialisation. Extend the actual provider adapter and export paths with bounded, loss-aware representations and live fixtures. Specialised types need explicit per-type treatment.
2. **DDL can partially apply.** TableDesignWindow.OnApply explicitly runs sequential ALTER statements without an owned transaction. The warning is honest, but the desired dependable designer requires atomicity for operations SQL Server supports transactionally, target/schema freshness checks and explicit recovery for operations that cannot be atomic. Confirm independently that failure on statement two preserves the original schema where atomicity is promised.
3. **Packaging configuration is incomplete.** The self-contained path publishes with a runtime identifier after a restore that supplies none. Sqloryn.csproj has no RuntimeIdentifiers declaration. Verify a clean restore/publish for each RID and lockfile instead of relying on cached assets. The default package requires .NET on the user machine. Add build provenance, notices and clean-Mac acceptance.
4. **Progress evidence understates some code and overstates some tests.** Correct stale CAP rows by inspection. Count named tests, distinguish model timing from editor response and process creation from input readiness.
5. **The live harness covers six Core scenarios.** Materialisation, export, PK create, edit conflicts, estimated INSERT and denied SHOWPLAN. It does not cover all eight desktop journeys or the future administration workflows. The runner currently permits CORE-PLAN-DENIED to skip. A denied-plan acceptance claim must require that case to pass on a fixture capable of creating the restricted principal.
6. **UI claims require real event paths.** Source scanning for names/handlers is useful as a wiring check. It cannot establish focus order, VoiceOver reading, actual shortcut dispatch, file-sheet behaviour, theme quality or a delayed Keychain event in an Avalonia window.
7. **Central window integration is growing.** MainWindow.axaml.cs is roughly 1,400 lines. Extend through small feature-local controllers/views and existing Core operations, keeping one owner for shared command integration. Avoid adding all forthcoming management workflows to this file.

## Review decision

Continue the integrated PR. The audited source is a functional preview foundation with extensive component safety work. The full product assignment remains open. Complete the [delivery contract](DELIVERY-CONTRACT.md), reconcile all CAP/SURF entries and verify the required targets before claiming completion. Preserve explicit external blockers and the deferred AI decision.
