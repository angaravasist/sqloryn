# Maintainer context map

Current experimental source. Owner: SQLoryn maintainer. Reviewed: 2026-10-31.

Start with [repository rules](../AGENTS.md) and [current state](STATE.md).
This repository, not previous chats or the original reference ZIP, is the working
source of truth. Update relevant documents in the same PR as behaviour.

| Question | Authoritative document |
|---|---|
| Product, users, boundaries | [Product](PRODUCT.md), [candidate catalogue](FEATURE_CATALOG.csv) |
| What actually works? | [Feature matrix](FEATURE_MATRIX.md), [evidence/current state](STATE.md) |
| Code map, data flow | [Architecture](ARCHITECTURE.md) |
| Why these choices? | [ADR-001](decisions/001-sqlclient-probe.md) |
| What must this experiment prove? | [SPEC-001](specs/001-sqlclient-probe.md) |
| How to build/test safely | [Testing](TESTING.md) |
| Dependencies and redistribution | [Dependency assessment](DEPENDENCIES.md) |
| Credentials, network, storage and unresolved risk | [Threat model](THREAT_MODEL.md) |
| Repository protections and releases | [Security baseline](SECURITY_BASELINE.md), [security policy](../SECURITY.md) |
| Contribution and review | [Contributing](../CONTRIBUTING.md) |

Stable identifiers: `CAP-*` are product candidates; `ADR-*` decisions;
`SPEC-*` bounded specifications; `PROBE-*` acceptance checks; `R-*` risks.
Catalogue entries are aspirations, not implemented features or a build order.
Do not promote component tests to server, desktop or release evidence.
