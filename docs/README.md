# Maintainer context map

Current experimental source. Owner: SQLoryn maintainer. Reviewed: 2026-11-03.

Start with [current state](STATE.md).
This repository, not previous chats or the original reference ZIP, is the working
source of truth. Update relevant documents in the same PR as behaviour.

| Question | Authoritative document |
|---|---|
| Taking over PR #7 at 573d749? | [Maintainer handoff](ssms/PR7-HANDOFF.md) and [frozen 192-capability inventory](ssms/PR7-HANDOFF-INVENTORY.csv); use the active queue for subsequent changes |
| What did the independent code review establish? | [Codebase audit](ssms/CODEBASE-AUDIT.md) |
| What should the Mac app look and feel like? | [Mac experience and screen acceptance](ssms/MAC-EXPERIENCE.md) |
| Product, users, boundaries | [Product](PRODUCT.md), [candidate catalogue](FEATURE_CATALOG.csv) |
| What does the Windows comparator expose, and how is the work distributed? | [Versioned SSMS inventory](ssms/README.md), [parallel feature TDD](ssms/PARALLEL-TDD.md) |
| What actually works? | [Feature matrix](FEATURE_MATRIX.md), [evidence/current state](STATE.md) |
| Code map, data flow | [Architecture](ARCHITECTURE.md) |
| Why these choices? | [ADR-001](decisions/001-sqlclient-probe.md), [ADR-002](decisions/002-macos-ssh-sql-probe.md), [ADR-003](decisions/003-desktop-avalonia.md), [ADR-004](decisions/004-macos-database-studio.md), [ADR-005](decisions/005-sql-editor-component.md) |
| What must this experiment prove? | [SPEC-001](specs/001-sqlclient-probe.md), [SPEC-002](specs/002-developer-preview-0.md), [SPEC-003](specs/003-macos-database-studio.md) |
| Offline journey/keyboard checklist and per-journey evidence | [Journeys](JOURNEYS.md), [Journey evidence](ssms/JOURNEY_EVIDENCE.md) |
| How to build/test safely | [Testing](TESTING.md), [Walkthrough](WALKTHROUGH.md) |
| Dependencies and redistribution | [Dependency assessment](DEPENDENCIES.md) |
| Credentials, network, storage and unresolved risk | [Threat model](THREAT_MODEL.md) |
| Repository protections and releases | [Security baseline](SECURITY_BASELINE.md), [security policy](../SECURITY.md) |
| Contribution and review | [Contributing](../CONTRIBUTING.md) |

Stable identifiers: `CAP-*` are product candidates; `ADR-*` decisions;
`SPEC-*` bounded specifications; `PROBE-*` acceptance checks; `R-*` risks.
Catalogue entries are aspirations, not implemented features or a build order.
Do not promote component tests to server, desktop or release evidence.
