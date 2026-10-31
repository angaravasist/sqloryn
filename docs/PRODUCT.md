# Product contract

Current intent, not a release claim. Owner: SQLoryn maintainer. Reviewed: 2026-10-31.

SQLoryn is an independently branded, local-first, macOS SQL Server management
application in discovery. Its long-term goal is broad, functional, SSMS-familiar
workflows: explicit connection context, object exploration, SQL editing/results,
schema development, diagnostics and administration where the target supports it.
Functional fidelity, data safety and accessible Mac interactions take priority
over visual imitation. There is no supported app or installer yet.

## Users and core jobs

- Developers: connect, understand objects, execute intended SQL, inspect accurate
  results, and save scripts without losing work.
- Troubleshooting engineers: use least privilege, cancel expensive work, inspect
  diagnostics and deliberately export evidence without exposing private data.
- DBAs: inspect capabilities/permissions, preview generated changes, understand
  partial failures, and administer supported targets without fake controls.
- Consultants: separate customer environments and maintain unmistakable target
  identity across tabs, reconnects and sleep/wake.
- Contributors and agents: trace requirements through decisions, code, tests
  and compatibility claims directly from the repository.

These are hypotheses from the supplied brief, not validated user interviews.

## Required behaviour

Bind execution to the visible session. Never silently weaken TLS or replay
potentially mutating SQL after an uncertain failure. Distinguish cancellation
requested from server completion. Product-generated destructive changes need
target, preview, confirmation and honest partial-failure reporting; a keyword
filter does not make arbitrary user SQL safe.

Preserve NULL/empty distinctions, decimal precision, Unicode, time offsets and
binary values. Make partial/truncated output explicit. Treat metadata, files and
results as untrusted. Core desktop flows must work with keyboard and VoiceOver;
dense trees/grids require device-level testing.

Core operation needs no SQLoryn account, telemetry or hosted intermediary.
Saved secrets require OS-secure storage; no persistence exists in the probe.
AI-first documentation is mandatory. Embedded AI, analytics, sync and third-party
data transmission require a separate opt-in design and security review.

## Scope and compatibility

[192 candidate capabilities](FEATURE_CATALOG.csv) retain the brief's stable IDs,
outcomes and negative cases in a shorter format. They are not an exhaustive SSMS
inventory. CAP-173 duplicates CAP-053's template intent and is an alias rather
than a second implementation commitment. Optional CAP-176 is outside the baseline.
Describe support per client OS/architecture, server version/product/edition,
auth mode, permissions and actual evidence.

SQL Server, Azure SQL Database, Managed Instance and BI services differ. Azure
SQL Database lacks SQL Server Agent; other targets have their own constraints
([Microsoft](https://learn.microsoft.com/en-us/azure/sql-database/sql-database-job-automation-overview)).
Windows-specific host/BI management remains unassessed, not promised.

## Reference-pack reconciliation

Read all 32 entries of `SQLoryn_Product_Brief_2026-10-30.zip`, beginning with
`00_START_HERE.md`; verified all 31 manifest-listed hashes, 192 capabilities and
24 risks. Archive SHA-256:
`9e8b419200404452f87b2d1b0802e26070debf32c30f17fc7fcb802667595002`.

Retained outcomes, stable IDs, privacy constraints and evidence levels. Reassessed
technology/licensing against primary sources in [ADR-001](decisions/001-sqlclient-probe.md).
Did not adopt its embedded agent prompt, suggested stack/order or claimed latest
SSMS version as instructions or facts. The existing real-connection gate is
independently justified by absent application code and unresolved driver evidence.
The archive stays outside the repository; no proprietary assets or documentation
text was imported. Naming clearance, exact binary rights, user research and full
SSMS reference observation remain open. No parity percentage is established.
