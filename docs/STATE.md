# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-11-04. Experimental; no release support.

## Work under review

Branch `codex/macos-database-studio` — macOS Database Studio integrated preview
([SPEC-003](specs/003-macos-database-studio.md), [ADR-004](decisions/004-macos-database-studio.md),
[PR7 acceptance](ssms/PR7-ACCEPTANCE.md)). Builds on merged Developer Preview 0 (PR #6 / `f561597`).
Draft [PR #7](https://github.com/angaravasist/sqloryn/pull/7). Tip under review: `78fba00`.

## Hosted checks on `78fba00`

| Check | Result |
|---|---|
| probe-components (ubuntu-24.04, macos-15, macos-15-intel) | PASS |
| repo-safeguards | PASS |
| CodeQL / Analyze (csharp, python, actions) | PASS |
| sql-integration | **SKIPPED** (no hosted SQL fixture) |
| Runs | [components](https://github.com/angaravasist/sqloryn/actions/runs/41739385983) · [CodeQL](https://github.com/angaravasist/sqloryn/actions/runs/41739380738) |

## PR #7 finding dispositions

Statuses below distinguish **component-tested** from **acceptance-complete**. Incomplete acceptance tests keep the finding **Open** or **Partial** even when Core assertions exist.

| # | Disposition | Evidence / gap |
|---|---|---|
| 1 | **Partial** — warnings out of `ExecutableSql` | Core hostile-identifier PASS; live disposable apply of hostile names **unverified** |
| 2 | **Open (reopened)** — generation-safe autofill implemented; native UI run pending | D8: `ProfileAutofillSession.TryApplySecret(generation, secret)` applies only for the current generation while Waiting; `ConnectAutofillController` models SelectProfile/UserTypedPassword/LookupCompleted/UserChangedServer/ResolveForConnect and `ConnectWindow` uses it. Component tests (typed→late Keychain, host change→late Keychain, Keychain→host change→Connect, A secret never reaches B) pass; not yet exercised in the native Avalonia window |
| 3 | **Open (reopened)** — batch rollback fakes pass; collation / dispose gaps | Verifier D3 (case-insensitive silent overwrite), D4 (dispose hides outcome); live SqlTransaction **unverified** |
| 4 | **Partial** — export destination + types | Core `export-from-resultset-types` PASS; independent verifier must recheck integrated UI export path (D1) |
| 5 | **Open (reopened)** — staged save without external-edit gate | Verifier D2: external file edit must survive Save |
| 6 | **Partial** — bounded QueryRaw | Core PASS; live MAX/xml **unverified** |
| 7 | **Open (reopened)** — preflight exists; selection→document line mapping incomplete | Verifier D5: error in selected batch must cite document line |
| 8–12 | **Partial** — shell implemented | Build only; VoiceOver / light-dark mid-session / Finder launch **unverified** |
| 13 | **Partial** — new-tab script SELECT | Core PASS; UI click path **unverified** |
| 14 | **Open** — OE truncation only | Keys, indexes, functions still incomplete |
| 15 | **Open** — create-table-with-PK + estimated plan / SHOWPLAN denial | Not implemented |
| 16 | **Partial** — icon required; plist lint; FD package | Clean-Mac install / SC licence audit **pending** |
| 17 | **Open** — a11y + performance measurements | No VoiceOver walk; no recorded timings |
| 18 | **Partial** — inventory/docs | CAP catalogue mostly candidates; sql-integration still skipped |

## Verifier defects in flight

| ID | Required closure |
|---|---|
| D2 | External edit of saved `.sql` survives Save (refuse or prompt; original external content preserved when refused) |
| D3 | Concurrent text change under case-insensitive collation cannot be silently overwritten |
| D4 | Transaction dispose cannot replace/hide known or uncertain commit outcome |
| D5 | Error/rejection inside a selection reports document-relative line |
| D8 | Delayed Keychain cannot overwrite manually entered password or send secret to changed endpoint; exercise actual UI event sequence |
| D1 recheck | Independent verifier re-runs integrated export path after D2–D8 land |

## Integrated local checks (tip `78fba00`)

| Check | Result |
|---|---|
| Local Release build + Core 72 PASS + probe + Python + preflight + FD `.app` | Recorded at tip push |
| Live disposable SQL / eight journeys | **Not run** — no authorised fixture present this session |
| Windows SSH product path | **Closed** |

## Explicit non-claims

- Not SSMS-compatible; not production-ready; not signed/notarised.
- No Windows SSH connection feature in the product UI.
- No Entra / Agent / AG / BI surfaces; CAP-176 AI remains deferred.
