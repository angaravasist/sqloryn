# SQL Server Management Studio comparison

The comparator is **SSMS 22.11.3 for Windows**, released 29 October 2026. Freeze this version for behavioural comparison. Record the installed workloads, architecture, target engine/service, edition, permissions and authentication alongside observations. Later SSMS versions get a delta review. SQLoryn's current implementation and compatibility evidence remain in the [feature matrix](../FEATURE_MATRIX.md) and [state](../STATE.md). [Microsoft release notes](https://learn.microsoft.com/en-us/ssms/release-notes-22) · [Release history](https://learn.microsoft.com/en-us/ssms/release-history)

## Three different units of coverage

| Register | What one row means | How to use it |
|---|---|---|
| [Action inventory](actions.csv) | One of 420 named action candidates in 41 families. Some intentionally overlap across different user entry points. | Filter by owner or family. Each has a stable ID, family-level sources, related product IDs and a failure case. Verify the individual entry point, prerequisites and exact semantics before promoting it to documented or observed SSMS behaviour. |
| [Source index](reference-index.csv) | A navigation destination, manual file, keyboard binding, component ID, release-note entry, or engine-manual reference. | Find the specific evidence for each action and locate actions missing from the initial expansion. Multiple articles, shortcuts and wizards can describe the same user operation. A document mentioning SSMS can also describe an external tool. |
| [Product crosswalk](cap-crosswalk.csv) | One of the existing 192 SQLoryn product capability IDs, assigned to a workstream. | Join to [the original product catalogue](../FEATURE_CATALOG.csv) by CAP ID. The latter retains its existing implementation assessments. Mac-specific additions, candidate extensions and deferred AI retain separate status. |

The action count is an inventory of work to reconcile, not an exhaustive SSMS feature count, estimate of effort, implemented count or parity percentage. Family references provide an entry into research, not proof of every individual action. Maintain IDs across edits; assign new IDs only for newly distinguished behaviours. Record aliases instead of counting multiple invocation paths as multiple implementations.

## Research coverage and reproducibility

Two complete, date-pinned upstream trees were used. Under the SSMS subtree, all Markdown articles outside include directories were indexed, including topics absent from navigation. The English TOC was traversed recursively for destinations, including cross-manual links. Exact lines in the keyboard, installer and 22.x release tables were indexed independently. Under the SQL Server manual's docs subtree the research scanned Markdown outside include directories for SSMS-specific file names, section headings and body mentions. The two engine passes deliberately overlap. Illustrations, include fragments and unmarked server operations require additional inspection. [Pinned SSMS manual](https://github.com/MicrosoftDocs/data-tools/tree/3f324cf4d37f5dd5b00f3301c3a652d2e4df435e/ssms) · [Pinned SSMS navigation](https://github.com/MicrosoftDocs/data-tools/blob/3f324cf4d37f5dd5b00f3301c3a652d2e4df435e/ssms/TOC.yml) · [Pinned SQL Server manual](https://github.com/MicrosoftDocs/sql-docs/tree/989afae1e7e6b1287b32c4fa34588b5d1146b9d8/docs)

| Source kind | Rows | Interpretation |
|---|---:|---|
| navigation | 558 | Unique destination URLs across the English SSMS navigation, including administrative, legacy and support references. |
| manual-file | 539 | SSMS articles, including generic help and earlier-version notes. |
| keyboard-binding | 207 | Individual shortcut-table rows. Context and source line identify the referenced operation. Preserve collisions across contexts. |
| installer-component | 15 | Component table entries, including msoledbsql. These include dependencies; see the workload manifest for group membership. |
| release-change | 172 | Individual entries under What's new in the 22.x notes, including libraries and platform servicing. Bug-fix tables remain available via the release article. |
| engine-manual-locator | 577 | Candidate SSMS-specific engine headings and filenames. |
| engine-text-mention | 1,161 | First matching body line in each additional scan result, including incidental references. |
| **All source locators** | **3,229** | Rows across the seven categories, with duplicates across kinds expected. |

Keep evidence readable without copying proprietary SSMS help text, assets or code. Record a source link and your own behavioural description. On refresh, re-resolve changed source anchors and manually examine additions, removals, deprecated paths, known issues and optional workloads; a changed hash or disappearing link is not proof that the feature was removed.

## What falls outside the obvious query editor

- **Windows shell and productivity.** Installation worksets, language packs, offline installs, options, shortcut schemes, presenter mode, document groups, registered servers/CMS, templates, solutions, external tools, Git and terminal entry points each need a disposition. Mac replacements should preserve the job and input/output contracts while using native menus, Keychain and document conventions. [SSMS keyboard reference](https://learn.microsoft.com/en-us/ssms/sql-server-management-studio-keyboard-shortcuts) · [Installer components](https://learn.microsoft.com/en-us/ssms/install/workload-component-ids)
- **Development and administration.** Query Designer and Database Diagram Designer have distinct surfaces. Database DevOps and schema compare have project/package workflows. Agent includes multiserver administration, alerts, operators and history. Diagnostic tools include query plans, Query Store, Extended Events and separate companion utilities. The engine manual provides additional backup, recovery, security, replication and HA procedures. [Components](https://learn.microsoft.com/en-us/ssms/components-features) · [Schema Compare](https://learn.microsoft.com/en-us/ssms/schema-compare) · [Agent](https://learn.microsoft.com/en-us/ssms/agent/sql-server-agent)
- **Service and workload boundaries.** Distinguish Database Engine connections from SSAS semantic-model sessions, SSISDB catalogue operations, SSIS service access and supported SSRS server tasks. Microsoft directs BI solution authoring to SSDT. Preserve this distinction in the ledger. A dedicated pool, Fabric SQL, Azure SQL Database or Managed Instance is a separate target. Management via portal or a separate Windows process is recorded as a handoff. [Microsoft component boundaries](https://learn.microsoft.com/en-us/ssms/components-features) · [Fabric SQL connection](https://learn.microsoft.com/en-us/ssms/quickstarts/ssms-connect-query-sql-database-fabric)
- **Version precedence.** A general overview can lag release notes: Database DevOps, Schema Compare, SQL Formatter and Copilot Agent Mode are described as GA in the 22.10.2 notes. This does not imply every AI subfeature is GA, available to every account, authorised for SQLoryn, or redistributable. Track each separately. SSMS's cloud or AI integrations confer no permission to transmit SQL. [Microsoft release deltas](https://learn.microsoft.com/en-us/ssms/release-notes-22#22102)

## How to close the undocumented gap

Create an observation ledger keyed by surface ID, SSMS version, installed profile, target/server version, principal, path, state, expected output/effect and source/evidence. Capture **default shell**, **optional Microsoft workload**, **companion tool**, **third-party extension**, **engine/service capability**, **legacy/removed** or **SQLoryn-specific** separately. Record availability and feasibility independently. Keep unobserved claims explicitly unresolved.

1. On an already authorised, isolated Windows comparator, record Help/About and workload manifest. Third-party extensions are a separate inventory. No Windows connectivity, account installation, licences or cloud access are implied by this document. If no comparator is available, continue source work and mark the visual sweep blocked.
2. Enumerate top-level and nested menus, all Tools/Options pages and search results, toolbar customise/command list, keyboard command list including unbound entries, editor modes, tool windows, property grids, dialogs, wizard steps, object context menus, details panes, report lists and object scripting destinations. Walk enabled, disabled, disconnected, connected, running, cancelled and error states. Preserve evidence of disabled commands without invoking them.
3. Repeat with different object types and supported targets, optional workloads and a restricted principal. Inspect current WWI or synthetic fixtures only. Use no production data. Record documented vs observed differences, edition gates and Windows-only dependencies. Record subactions, options and aliases with parent IDs. Each destructive or external action needs separate authorisation before execution.
4. Disposition every source locator and observed command as covered, alias, new surface, prerequisite/reference, external, historical, or unresolved. Link each canonical action to direct evidence and to a test or specific blocker. Reconcile in both directions: every observed UI action has a row, every claim of actual SSMS support has direct evidence. Keep crosswalk for unsupported engines and features; never remove rows to improve a completion percentage.
5. Coverage sign-off requires zero unexplained items for the stated configuration and disclosure of every uninspected configuration. Documentation mining cannot prove all commands in a live Windows binary. Query success on Mac cannot prove feature parity. Compare outputs and side effects for both products on a matching synthetic fixture.

## Cursor assignment

Use [parallel feature ownership and TDD](PARALLEL-TDD.md). Keep the existing Mac redesign, data-safety findings, packaging requirements and acceptance journeys in scope. This register expands the coverage contract; it does not approve new infrastructure, Windows connectivity, new vendors, proprietary binaries or a release.
