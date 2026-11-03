# SPEC-003 — macOS Database Studio (integrated preview)

**Status:** Accepted for implementation on branch `codex/macos-database-studio`  
**Base:** `origin/main` @ `f561597` (merged Developer Preview 0 / PR #6)  
**Non-goals:** Windows SSH recreation; release publish; production-ready claims; Entra/Kerberos beyond honest stubs; SSMS parity claims without live evidence.

## Journeys (acceptance)

| Journey | User-visible outcome | CAP anchors | Evidence bar |
|---|---|---|---|
| J1 Connect & remember | Save non-secret profile; optional Keychain password; encrypt-required connect; fail closed on trust errors | CAP-002, CAP-013–015, CAP-017–018 | Unit + offline UI; live SQL when available |
| J2 Object Explorer | Expand databases → tables/views/procs → columns; refresh; script SELECT; properties read-only | CAP-033–042 (subset) | Unit metadata SQL builders; live expand when available |
| J3 Author & run | Tabs; dirty Open/Quit; find/replace; F5 selection-or-document; `GO` batches; cancel vs timeout vs unobserved | CAP-050–058, CAP-066–076 | Deterministic Core + UI status strings |
| J4 Results & export | Grid (NULL≠empty); multi-result tabs; CSV/JSON export; copy | CAP-083–095 (subset) | Unit export/fidelity; offline UI |
| J5 Design table | Column list edit → preview DDL → confirm → apply; explain partial failure | CAP-100–105 (subset) | Unit DDL preview; live apply when available |
| J6 Edit rows | Keyed edit; concurrency conflict surfaced; confirm before write | CAP-110–115 (subset) | Unit concurrency; live when available |
| J7 Diagnostics | Redacted support bundle; no secrets in logs/export | CAP-SEC / AC-SEC-01 | Unit redaction |
| J8 Package | Original icon; Info.plist; arm64 `.app` build script; docs honesty | Packaging | Local `build_macos_app.sh` |

## Boundaries

- No Windows SSH tunnel feature recreation (PROBE-11 remains optional lab tooling only).
- No credentials in repo/fixtures/CI logs.
- Destructive design/edit requires preview + confirmation.
- Candidate CAPs stay “not claimed” until tests + optional live evidence exist.

## Offline verification gate

`dotnet test` Core + App unit suites PASS; `scripts/build_macos_app.sh` produces launchable `.app` on the build Mac.
