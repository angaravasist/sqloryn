# SPEC-003 — macOS Database Studio (integrated preview)

**Status:** Accepted for implementation on branch `codex/macos-database-studio`  
**Base:** `origin/main` @ `f561597` (merged Developer Preview 0 / PR #6)  
**Non-goals:** Windows SSH recreation; release publish; production-ready claims; Entra/Kerberos beyond honest stubs; SSMS parity claims without live evidence.

## Journeys (acceptance)

| Journey | User-visible outcome | CAP anchors | Evidence bar |
|---|---|---|---|
| J1 Connect & remember | Save non-secret profile; optional Keychain password; encrypt-required connect; fail closed on trust errors | CAP-002, CAP-013, CAP-015, CAP-017–018 | Unit + offline UI; live SQL when available |
| J2 Object Explorer | Expand databases → tables/views/procs → columns; refresh; script SELECT; properties read-only | CAP-022, CAP-024–028, CAP-030–031, CAP-042 (subset; list/script only) | Unit metadata SQL builders; live expand when available |
| J3 Author & run | Tabs; dirty Open/Quit; find/replace; F5 selection-or-document; `GO` batches; cancel vs timeout vs unobserved | CAP-044–045, CAP-051–052, CAP-055–058, CAP-061, CAP-066–070 | Deterministic Core + UI status strings |
| J4 Results & export | Grid (NULL≠empty); multi-result tabs; CSV/JSON export; copy | CAP-083, CAP-085–086, CAP-088–089, CAP-092–093, CAP-095, CAP-100–101 (subset) | Unit export/fidelity; offline UI |
| J5 Design table | Column list edit → preview DDL → confirm → apply; explain partial failure | CAP-104–105, CAP-107, CAP-110 (subset) | Unit DDL preview; live apply when available |
| J6 Edit rows | Keyed edit; concurrency conflict surfaced; confirm before write | CAP-124 (Editable table data); CAP-080 for parameterised SQL | Unit concurrency; live when available |
| J7 Diagnostics | **Partial.** Redacted support bundle (no secrets in logs/export) and Query > Display Estimated Plan (SHOWPLAN_XML text, never executes the SQL). The original journey — permission-aware read-only database properties, blocking sessions, principal and job history, bounded plan viewer — is **not met**; redaction is not a substitute for it | CAP-021 (bundle only), CAP-152 (estimated text only), AC-SEC-01 | Unit redaction; estimate-plan fakes; live SHOWPLAN pending |
| J8 Package | Original icon; Info.plist; arm64 `.app` build script; docs honesty | Packaging | Local `build_macos_app.sh` |

CAP anchors are IDs from [FEATURE_CATALOG.csv](../FEATURE_CATALOG.csv). An earlier revision of this table used
ranges that did not match the catalogue (for example edit-rows pointed at CAP-110–115, omitting CAP-124); the
assessments for the rows above were realigned to the catalogue on the evidence named in each row, and rows with no
evidence (for example CAP-033, CAP-034, CAP-050) were returned to unassessed. A mapped row is still not a support
claim: see [STATE.md](../STATE.md) for what has run.

## Boundaries

- No Windows SSH tunnel feature recreation (PROBE-11 remains optional lab tooling only).
- No credentials in repo/fixtures/CI logs.
- Destructive design/edit requires preview + confirmation.
- Candidate CAPs stay “not claimed” until tests + optional live evidence exist.

## Offline verification gate

`dotnet test` Core + App unit suites PASS; `scripts/build_macos_app.sh` produces launchable `.app` on the build Mac.
