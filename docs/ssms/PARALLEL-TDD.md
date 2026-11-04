# One integrated PR, feature-owned tests

The [full delivery contract](DELIVERY-CONTRACT.md) defines the active implementation scope and completion conditions. This document supplies ownership and test patterns.

Start a local Agent session with the [Cursor assignment](CURSOR.md). Cursor is the implementation lead. Continue on the existing feature PR after checking its live head. Deliver working features and documentation together. No sequence of separate milestone PRs or routine per-feature permission requests. Treat the 18 ownership groups in actions.csv as concurrent queues. Use a subagent for each independent feature or closely coupled family as agent capacity permits. Keep one integrator responsible for conflicts, behaviour across families and the final PR. Start unblocked work immediately. Never ship a stub control to satisfy an inventory row.

## Parallel ownership

| Queue | Owns | Dependencies to agree across agents |
|---|---|---|
| Shell | Mac identity, menus, shortcuts, installer, welcome, system appearance, accessibility | Use shared command IDs with actions and labels, stable document/target bindings, evidence by architecture. |
| Connect | TLS, authentication, endpoint identity, Keychain, profile isolation | Publish verified session identity, target generation, disconnect and credential invalidation contracts. |
| Explorer | Tree, search, properties, scripting from selection, restricted metadata | Return scope, provenance and completeness; generators receive quoted identifiers from the originating session. |
| Editor | Text, documents, selections, formatter, templates, solutions | Preserve source offsets/file versions; never inspect the network just to type. |
| Engine | GO/SQLCMD policy, batches, transaction and cancellation semantics | Own execution contract and exact completion states; never replay unknown writes. |
| Results | Typed result sets, precise grid and loss-aware export | Preserve ordinals, unknown totals, truncation and atomic file boundaries. |
| Schema | Designers, generated DDL, keys, diagrams and controlled row edits | Own preview plans and conflict-safe write path. Disabled until verified safe. |
| Deploy | Project and package operations, comparison, Git-facing workflows | Distinguish preview from apply; licence review before adding a parser, deployment engine or vendor component. |
| Move | Flat-file and provider-based import/export, assessment, migration | Clarify client vs server paths, transactional boundaries and ownership of partial outputs. |
| Identity | Principals, permissions, audits and encryption | Least privilege, no secrets in diagnostics, compare actual vs requested state. |
| Operations | Backup/restore, configuration, maintenance, policies, mail and catalog-specific settings | Declarative previews, effect confirmation, restore validation and service capability probes. |
| Agent | Jobs, steps, schedules, operators, alerts, multiserver targeting | Shared operations must surface server-side job state, retries, permissions and target-specific outcomes. |
| Observe | Plans, statistics, monitoring, XE, Query Store, logs, companion tools | No implicit second execution; separate estimated and actual operations; bound sensitive payloads. |
| HA | AG, failover, mirroring, log shipping | Explicit data-loss boundaries; no active topology changes without destructive-test approval. |
| Replication | Distributor, publisher/subscriber, agents, monitor and conflict resolution | Require isolated multi-node test and authoritative downstream verification. |
| BI | SSIS, SSAS, SSRS and the SSDT boundary | Separate wire protocols, optional services and redistribution constraints; SQL connectivity alone proves none of these. |
| Cloud | Azure/Fabric/Synapse resource and identity workflows | Opt-in, tenant/target isolation, authorised fixtures, no spending or firewall modification. |
| AI | Inventory and privacy design of explicitly deferred assistant features | Keep CAP-176 deferred until maintainer approves implementation and provider/data boundaries. |

Each worker owns feature-local files and tests. Agree small data contracts in the existing Core and App architecture; nominate one editor each for shared session state, central UI routing, package manifests/lockfiles, evidence ledgers and CI. Exchange contract tests before integrating producers and consumers. Parallel execution must not produce 18 frameworks, separate executors or conflicting changes to one MainWindow. Use test fixtures that can run independently with unique names and ownership-scoped cleanup.

## Per-feature TDD contract

For every canonical action, record a stable surface ID, direct reference, comparator/configuration, its CAP links, actual user entry point, prerequisites, desired Mac behaviour, exact observable outcome, forbidden side effects, test ID, owner and status. Add negative cases from the family and CAP catalogue. Source locators that are aliases, install dependencies, historical or blocked get dispositions and evidence rather than invented implementation tests.

- **Red:** exercise the normal public entry point on the smallest faithful fixture; capture exact failing assertion. Add a relevant negative or uncertainty test, including missing permission/feature, corrupt input, interrupted write, duplicate request, stale target or data loss. Verify failure for the intended reason.
- **Green:** implement the shipping behaviour end to end. Generate previews independently of execution. Use real OS/provider adapters at integration boundaries, synthetic disposable targets and least privilege. Do not modify tests to accept the failure. If execution requires unavailable infrastructure, label the missing environment and continue independent work.
- **Refactor and integrate:** preserve red/green evidence at the feature commit, test through the actual UI or supported public API, rerun neighbouring features and cross-family journeys on the integrated head. Test cleanup on failure. Do not turn source coverage, mock-only tests, Linux SQL or a successful build into native Mac evidence.
- **Done for a feature:** a user can find, use and recover from the action, supported platforms and negative cases are documented, outcome and side effects match evidence, relevant accessibility/scale/security tests pass and an independent reviewer can repeat it. Maintain separate states for identified, documented, Windows-observed, implemented, component-tested, integrated/live-tested, blocked and explicitly deferred. Never label an unverified checkbox as supported.

### Concrete test contracts

| Feature | First failing test | Integration closure |
|---|---|---|
| Estimated plan | Denied SHOWPLAN on a proposed INSERT must never execute the INSERT. | A second connection confirms sentinel unchanged for both granted and denied estimate paths. |
| Group execution | Two targets, one offline, produce two attributable outcomes. | A synthetic write occurs at most once per explicitly selected target; unknown outcomes are not replayed. |
| Keyed edit | Two edits with a conflict on the second leave both rows unchanged. | Observe database from independent session, including trigger output and uncertain-commit handling. |
| Restore | Detect wrong backup chain and conflicting server-side file before confirm. | Restore into a unique disposable database and verify known synthetic values, then verify exact cleanup. |
| SSAS processing | Denied processing permission cannot surface as success. | An authorised SSAS fixture reports actual processing state; a relational mock cannot satisfy the test. |
| Schema compare | Reversing source and target changes the proposed operation and labels. | Preview changes nothing; verified apply affects only named target and respects exclusions. |

## Integrated acceptance

Use the [18 review findings and eight desktop journeys](PR7-ACCEPTANCE.md) and the [56-product design study](DESIGN-RESEARCH.md) for native UI review and acceptance. Add journeys combining restore → inspect schema → query → compare/export; restricted login → inspect visible objects → denied action; synthetic Agent job → run → inspect history → cleanup; and one target-qualified journey per BI/cloud/HA family claimed as supported. Preserve Windows SSH removal. Keep public-release signing, spending, credentials and publishing behind existing approvals. Never merge on the maintainer's behalf.

Update the product contract, feature matrix, action-to-test evidence, architecture, dependency/notice inventory, threat model, testing runbook, walkthrough and STATE wherever actual behaviour changes. One final report must show accepted and unresolved actions by scope, source/command coverage, new blockers, negative tests, exact builds/CI, native desktop evidence and outstanding release decisions. All feasible independent work proceeds on the same branch while externally blocked items retain explicit owners.
