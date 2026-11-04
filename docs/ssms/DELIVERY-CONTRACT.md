# SQLoryn complete-product assignment for Cursor

## Authority and objective

The maintainer wants an installable macOS SQL Server studio with SSMS-familiar workflows, a polished Mac interface and dependable database behaviour. Continue the existing integrated PR. This contract replaces the shorter incremental follow-up prompts as the active implementation assignment. AGENTS.md safeguards and the explicitly deferred AI scope still apply. Read CODEBASE-AUDIT.md for the inspected baseline, then check the live branch and implementation before changing anything.

Success means users can complete the documented workflows through the shipped app, failure paths preserve their data, and a fresh contributor can reproduce the claims. Completing one correction list does not finish this assignment. All 192 CAP rows and 420 SURF candidates remain accounted for, including aliases and explicit external/service prerequisites. Full parity must be scoped to tested versions and configurations.

Use one integrator, independent local worktrees for writers and one feature PR. Start ready work concurrently. Agree shared contracts before merging producers and consumers. Planning and acceptance criteria live with implementation in the PR. Routine reversible engineering choices are delegated to you.

## Required Mac experience

Implement [MAC-EXPERIENCE.md](MAC-EXPERIENCE.md) throughout all workstreams. Its screen contracts, visual system and native interaction acceptance are part of feature completion. Pair each management operation with its real discoverable UI. A shell-only restyle does not close the product assignment.

## Working queue and stopping rule

At the beginning, reconcile every CAP against source, entry point and tests. Use FEATURE_CATALOG.csv, actions.csv and cap-crosswalk.csv without replacing stable IDs. Add columns or a companion evidence table for owner, actual entry point, implementation symbols, remaining subactions, named tests, platform/target, result, evidence SHA, dependency and precise blocker. Existing assessments are starting evidence to check.

For each workstream below, create concrete tasks whose result a user can observe. Each task must identify files/contracts and positive/failure cases before a worker starts. The integrator keeps ready work available while workers return. Dispatch two to four writers initially and adjust to machine capacity and supported Cursor concurrency. The eighteen queues are coverage categories, not a requirement for eighteen simultaneous agents.

Use separate local worktrees for concurrent writes. Checkpoint intended handoff files before creating worktrees so workers receive them. Reserve central command routing, packages/lockfiles, CI and evidence consolidation for the integrator. Review a worker's diff and tests before integrating its intended commits. Run cross-feature tests after integration. Resolve conflicts using behaviour and tests, preserving unrelated edits.

A worker finishes its feature when the normal UI/service path and its tests work. The integrator then assigns the next ready task. Continue until all feasible implementation is complete, or every remaining task has a specific external dependency that actually prevents progress. Lack of a SQL fixture blocks a live SQL test, not unrelated editor, document, UI or component implementation. An agent/tool/context limit is a resumable checkpoint with exact next tasks, not product completion.

Final completion requires zero unexplained catalogue entries, no pending implementation within the agreed supported scope, all required acceptance checks passed on the integrated SHA, reviewed release artifacts and an explicit list of externally gated capabilities. A blocked feature remains incomplete. Never improve the completion report by silently excluding it. Keep separate counts of code present, component verified, native/live verified, missing and blocked. Do not convert these unequal counts into an overall percentage.

## Architecture and code standards

Retain the Avalonia App → in-process Core → SqlClient boundary unless evidence justifies a short ADR. Reuse SqlSession, typed/bounded readers, ScriptDocument, SafeFileWriter, profile/secret stores and existing result/operation models. New management features belong in feature-local views/controllers and Core operations. Keep SQL and persistence outside view event handlers. Avoid a second SQL executor for tests or a new plugin framework merely to distribute agent work.

Define only the contracts needed by active features. Every async operation owns its document/session/target generation, cancellation and result. Each generated mutation records the precise target, previewed inputs, required capability, confirmation and observed effect. Revalidate metadata and target before applying a stale preview. Never retry an operation with an unknown write outcome automatically. Separate SQL-authentication transport from server-version/edition/permission support.

Parameterise values and quote identifiers. Identifiers in SQL comments, labels and filenames require their own context handling. Use exact type semantics and bounded output. Never write back truncated data. Treat metadata, plans, SQL files and import/package input as untrusted. Restrict file paths and external process invocation to the approved operation. Secrets belong in memory or an explicitly chosen Keychain record, with endpoint ownership and late-response protection.

Follow .editorconfig, nullable analysis, warnings as errors and locked dependencies. Prefer existing abstractions and supported upstream components. Evaluate parser/editor, SMO, DacFx and service libraries for exact package licence, redistribution, macOS architectures, target support, cancellation and maintenance before adoption. Record chosen package versions and rejected alternatives briefly. Reuse package functionality only when it satisfies the same safety contract. Implement narrowly where a dependency would create more risk.

## Workstream contracts

Each section gives minimum behaviours to deliver. The CAP/SURF catalogue supplies the complete detailed checklist, including subactions beyond these examples. All implementation includes contextual help, accessible labels, loading/empty/denied/error states and recovery.

### 1. Connections, profiles and capabilities

Cover CAP-001–021. Finish SQL auth, groups, recents, multiple sessions, explicit reconnect, target/database switching and useful connection diagnostics. Add supported Entra and integrated-auth methods through a documented cross-platform provider path where feasible. Model provider/service identity separately from the relational connection. Detect target version, edition and available services/permissions rather than enabling every command from a successful login.

Keep profile JSON secret-free. Maintain explicit TLS mode and certificate validation. Accept valid database identifiers through structured SqlClient configuration. Preserve profile identity on rename and isolate secret retrieval from editable endpoint fields. Tenant/token refresh must not send credentials to a changed destination.

Tests include wrong password, expired token where authorised, denied Keychain, stale retrieval, endpoint edit, cancellation during connect, deleted profile, pin mismatch, duplicate database names, two simultaneous sessions and tab-specific target switching. Every unsupported method names the dependency and platform limitation.

### 2. Explorer, properties and scripting

Cover CAP-022–043. Add searchable/filterable object navigation and subtree refresh without losing focus. Expand foreign keys, constraints, triggers, sequences, synonyms, types, full-text objects and security/management trees as capabilities permit. Implement definitions and properties for views, procedures and functions, index columns/includes/filter, key order and constraint relationships. Generate CREATE/ALTER/DROP/SELECT scripts where applicable into a correctly targeted document.

Use lazy cancellable bounded reads with honest continuation/truncation. A metadata-permission denial must not look like an empty database. Handle names containing whitespace, Unicode, newlines, apostrophes and closing brackets. Tests cover duplicate names across schemas/databases, restricted visibility, changed target during expansion, object removal during refresh and large catalogues.

### 3. Editor and documents

Cover CAP-044–065 and CAP-173. Choose a maintained compatible editor component after licence review. Deliver syntax colours, line numbers, go-to-line, selection-aware diagnostics, keyboard editing, folding, search/replace and snippets. Add context-aware completion and formatting with explicit supported syntax. Preserve source offsets and comments and never make typing require a network connection.

Implement session/document recovery with opt-in storage rules, atomic writes and bounded retention. SQL history and recovery can contain confidential text, so keep them local, discoverable and clearable. Preserve encoding/newline policy, external edits, Save As cancellation and multiple tabs. Add explicit database selection, per-tab options and visible transaction state.

SQLCMD directives need a deliberate supported subset. File inclusion stays within a user-authorised scope. OS command execution cannot be silently enabled. Before first execution, reject unsupported directives with document locations. Test GO in comments/strings, repetitions, quoted identifiers, late malformed batches, 2 MB scripts, interrupted saves and recovery after a forced app exit using synthetic documents.

### 4. Query execution and data fidelity

Cover CAP-066–101. Finish the production reader's type matrix, including time, uniqueidentifier, sql_variant and supported special/version-specific types. Retain decimal(38), money scale, timestamp offsets, Unicode and NULL distinctions through display, copy and export. For a type requiring an unavailable provider, report an explicit cell representation without pretending to decode it.

Ensure batch errors, notices, affected rows and original document coordinates survive multiple result sets and GO. Preserve prior completed rows when later reads fail. Cancel, timeout, transport failure and unknown completion must remain distinguishable. Expose transaction state without implicit commit/rollback of user transactions.

Deliver results-to-text, cell detail viewers, copy rows/headers, local sort/filter, result navigation and streaming export. Label sorting/filtering as applying to retained rows when the result is truncated. Streaming all results to disk must be a deliberate execution/output mode and must never rerun a mutation to recover discarded output. Cancel/fail leaves an existing destination intact. Test huge cells, huge column counts, multiple empty sets, duplicate headers, formula-like CSV cells and output budgets.

### 5. Schema, object editors and data editing

Cover CAP-102–124. Complete create/alter/drop database and table workflows, columns, PK/FK/unique/check/default constraints, indexes, identity/computed definitions, view/procedure/function/trigger editors, sequences/synonyms/types and dependency-aware scripts. Show the exact target and executable preview before mutation.

For transactional DDL, use an owned transaction with validation and rollback on failure. Reject or explicitly separate operations whose platform semantics prevent atomicity. Detect changes between loading schema, preview and apply. Preserve existing constraints and dependencies unless the preview explicitly changes them. A designer must not discard unmodelled attributes.

Extend safe row editing to supported insert/delete operations with explicit confirmation, typed parameters, keys and dependable conflict checks. Test AFTER/INSTEAD OF triggers, nullable keys where applicable, case/trailing-space changes, schema change during edit, failure on the second row, uncertain commit, disconnect and reload. Verify independently from another session.

Implement diagrams and schema comparison as real metadata-backed views with source/target direction, differences, exclusions and dependency ordering. Compare/apply must preserve data according to the reviewed preview. SQL projects and DACPAC/BACPAC operations require their real supported engine and end-to-end verification.

### 6. Import and transfer

Cover CAP-119–123 and migration candidates. Implement file selection, encoding/delimiter/header preview, source/target mapping, type/null conversion, error reporting and clear append/create/replace semantics. Preview reads must not write. Make transaction size, partial-import recovery and cancellation explicit. Keep server filesystem paths distinct from paths on the Mac.

Tests include quoted newlines, invalid encoding, malformed dates, culture-dependent numbers, duplicate keys, rollback/partial batch reporting, existing destination protection and interruption. Package imports must validate target direction, contents and effects. A generic SQL text dialog does not satisfy an import wizard.

### 7. Security administration

Cover CAP-125–134. Deliver logins/users, role membership, mappings, schemas/ownership and GRANT/DENY/REVOKE workflows with current versus proposed permissions. Include contained users, credentials/proxies and auditing where the target supports them. Redact secret inputs and never store passwords in generated-script history or diagnostics.

Use restricted principals for metadata/permission tests. Previewing must leave permissions unchanged. Verify a role change from a separate principal, denied operations, partial failure, secret replacement and server versus database scope. Never claim Windows-domain or tenant integration from a SQL-auth fixture.

### 8. Operations and maintenance

Cover CAP-135–146 and CAP-155/156/159–162. Deliver backup/restore, files/filegroups, recovery/compatibility options, integrity checks, index maintenance, server configuration, logs/reports, linked servers, mail and policy operations where supported. Separate server-side paths and credentials from local files. Explain asynchronous server progress and operation completion.

Restore must validate backup sets, chain, target files and overwrite intent. Use a uniquely named disposable database to verify restored synthetic values. Test wrong chain, permissions, unavailable files, conflicting destination and uncertain operation outcome. Maintenance requires bounded/cancellable status and honest completion. Never interpret connection loss as proof an operation stopped.

### 9. Agent and observability

Cover CAP-147–154. Add Agent jobs, steps, schedules, history, alerts/operators and execution status with server capability gates. Preview edits, require confirmation for running/deleting work, and show server-side outcome independently of a client timeout. Test denied roles, schedules/time zones, disabled Agent, step failure and exact resource cleanup.

Finish estimated and actual plans, a safe XML parser, graphical operators/properties and save/open plans. Estimated plans must never execute submitted SQL. Actual-plan capture executes exactly once with explicit user intent. Add statistics, Query Store, XE, activity/blocking and diagnostics with version/permission gates, bounded polling and cancellation. Treat plans and captured statements as potentially sensitive. Restrict XML external entities and file/network resolution. Retest SHOWPLAN reset failures and taint from every exit path.

### 10. High availability and replication

Cover CAP-157/158 and corresponding SURF actions. Implement capability-aware topology/health and configuration workflows for the supported targets, including log shipping/mirroring where the pinned comparator exposes them. Replication includes publisher/distributor/subscriber, agent state, monitoring and conflict visibility. Failover and topology changes need explicit impact previews and isolated multi-node acceptance. No single-server mock can qualify these operations.

When the test topology or protocol is unavailable, finish independent UI/validation/model work and document the concrete missing fixture. Retain the feature as incomplete until the real operation and recovery are verified. Continue other queues.

### 11. BI, cloud and associated tooling

Cover CAP-163–172/174/177 and pinned comparator workload boundaries. SSISDB catalogue/deployment/execution, SSAS XMLA/MDX/DAX and processing, and supported SSRS administration each need their own protocol and permissions. Preserve the distinction between SSMS administration and SSDT authoring. Build genuine workflows for supported services after licensing/platform investigation.

Azure/Fabric browsing and identity require explicit tenant/resource selection and data boundaries. Source-control operations need diff/commit/branch handling without automatic remote publication. Notebook candidates require the existing scope decision. No provider subscription, paid resource, new credential or cloud upload is implied by this assignment. CAP-176 AI remains explicitly deferred.

### 12. Mac experience and distribution

Cover CAP-175/178–192 and all original UI findings. Use system theme/accent and a disciplined type/spacing palette. First launch has one clear connection action, recent profiles and an approachable empty state. Place current target and query status consistently. Make the editor, Explorer and results resize cleanly at laptop size with useful focus and keyboard traversal. Ensure menus, toolbar and context actions share availability and outcome.

Keep the original icon legible in Dock/Finder. Use native document dialogs, window lifecycle and shortcuts. Test dark/light/system appearance, scale, keyboard-only navigation, VoiceOver order, resize, sleep/wake and dirty close/quit. Add session recovery and multi-window semantics without sharing the wrong connection or buffer.

Build a self-contained installable candidate for arm64 and x64 using locked RID-correct restore. Include licences, notices, SBOM, version/source commit and hashes. Test the package on a Mac without a preinstalled .NET runtime. Prepare reproducible signing/notarisation instructions and inputs for maintainer approval. Release signing keys, publication and purchases remain maintainer decisions. Updates, if implemented, require authenticated artifacts and rollback/downgrade policy.

## Verification that closes work

Use test-first changes for each feature and regression. The first failure must demonstrate the missing behaviour, not just a missing method. Test the actual UI/controller/Core entry point and real side effects at integration boundaries. Mocks support deterministic race/failure tests. They do not establish database or macOS behaviour.

Preserve the existing eight journeys and add an integrated journey for each delivered management family. Include restricted principals, hostile metadata, network interruption, large outputs and ownership-scoped cleanup. Run the shipping Core harness on the existing isolated Linux x64 workflow. The maintainer previously approved a disposable GitHub-hosted SQL test. Check that the current invocation stays within that consent and its licence/test scope, then use it. If extending beyond that scope requires consent, request only that specific expansion while independent work continues. Do not reinstate Windows SSH/LAN forwarding.

Require every mandatory CORE assertion, including denied SHOWPLAN when the controlled fixture supports it. Record exact SHA, workflow/run, target, platform, pass/fail/skip and cleanup. Linux results establish Linux behaviour. Native arm64/x64 SQL and actual Mac UI still require their own evidence.

Use available native automation for offline UI checks. Check permissions in the current session rather than inheriting an earlier denial. Screenshots, accessibility tree and spoken VoiceOver are distinct observations. An unavailable screenshot does not prevent keyboard or component work. Test the actual packaged build and record its provenance.

Run locked restore/build, Core/provider executables, Python tests, preflight, dependency audit, packaging and hosted checks on the integrated commit. Distinguish named assertions from summary lines. Measure input-ready launch and editor/grid responsiveness with fixture sizes and hardware. Set and document reasonable performance budgets before measuring. Correct real regressions, not just the reported number.

## AI-first documentation and independent review

Update README, STATE, FEATURE_MATRIX, FEATURE_CATALOG, the action evidence ledger, specs, relevant ADRs, architecture, dependency/notices inventory, threat model, testing and walkthrough whenever behaviour changes. The next contributor must find the code symbol, command and test behind each supported claim without this chat. Record aliases and per-target limitations instead of duplicating claims. Keep secrets and customer SQL out of evidence.

Invoke sqloryn-verifier after integration for independent review of credentials, writes, documents, result fidelity, cancellation and release artifacts. Have a worker other than the implementer review each sensitive feature. Reproduce findings, fix the shared cause and rerun the affected tests. The integrator owns cross-feature regressions and rechecks hosted CI after corrections.

Return a final report containing the app/install path, actual user workflows, screenshots from the qualified build, the integrated SHA, tests and cleanup evidence, unresolved catalogue items by owner and exact blocker, and maintainer decisions. A progress checkpoint includes the remaining ready queue and next task. Merge and release only after maintainer approval.
