# Eight journey evidence

Owner: SQLoryn maintainer. Reviewed: 2026-11-04. Experimental; no release support.

One section per integrated journey in [PR7-ACCEPTANCE.md](PR7-ACCEPTANCE.md#integrated-user-journeys). Companion
checklist: [../JOURNEYS.md](../JOURNEYS.md). Launch numbers: [../evidence/launch-arm64.md](../evidence/launch-arm64.md).

**No live SQL Server was used. No journey is complete.** There is no approved disposable SQL host, so every step that
needs a connection is Skip. Screenshots and VoiceOver are Skip because Screen Recording and VoiceOver access are not
available to the agent that produced this file.

Status meanings:

- **Pass** — an automated check ran and passed. The reason names it. "Core" means a component assertion in
  `tests/Sqloryn.Core.Tests` against fakes/pure functions: it supports, but never proves, the native or live step.
- **Skip** — not run; the reason says why.
- **Fail** — run and failed. None recorded.

Last Core run: 109 named assertions printed `PASS` plus the summary line, 0 `FAIL` (`dotnet Sqloryn.Core.Tests.dll`), on macOS 27.2 arm64.

## 1. Install/open and identify

| Step | Status | Reason |
|---|---|---|
| Build an arm64 bundle with icon and Info.plist | Pass | `scripts/build_macos_app.sh` completed (framework-dependent); the script fails on a missing icon and checks required plist keys |
| Both architecture bundles have icon and expected plist | Skip | Only the arm64 bundle was built; no x64 build or inspection |
| Native arm64 process starts offline | Pass | Process-start only, 3 runs, median 127 ms, max 169 ms ([launch-arm64.md](../evidence/launch-arm64.md)); window and input readiness were not observed |
| Icon legible at small size | Skip | Screen Recording denied; no screenshot |
| Keyboard-only navigation | Skip | Not walked; targets and names are listed in [JOURNEYS.md](../JOURNEYS.md) and the Core `a11y-keyboard-contract-source-scan` checks that they exist in source |
| System menu/VoiceOver name | Skip | VoiceOver and accessibility-tree inspection not performed |
| Missing-runtime guidance for a framework-dependent package | Skip | Not tested without a runtime |
| Separate Intel publish from Intel launch | Skip | No Intel hardware or build in this run |

## 2. Connect and target

| Step | Status | Reason |
|---|---|---|
| Two tabs keep separate SQL/targets | Pass | Core `tab-sql-isolation`, `target-verification-separated` |
| Connect and show the actual target | Skip | no disposable SQL |
| Change one tab's database without the other | Skip | no disposable SQL (state isolation covered only by Core `tab-sql-isolation`) |
| Saved profiles persist without passwords | Pass | Core `connection-profiles` (store round-trip; no password in JSON); two-profile UI flow Skip: no UI automation |
| Forget a saved secret, reconnect with an explicit credential | Pass | Core `autofill-forget`, `ctl-denied-typed-forget-ordering`, `ctl-typed-then-late-keychain` (fake secret store) |
| Late async response after endpoint change | Pass | Core `autofill-delayed-a-to-b`, `ctl-change-host-then-late-keychain`, `ctl-delayed-store-sequence` |
| Keychain denial | Pass | Core `autofill-keychain-denial`, `ctl-denied-typed-forget-ordering` use a fake store; real macOS Keychain is Skip: not exercised |
| Wrong password | Skip | no disposable SQL |
| Certificate pin mismatch | Skip | no disposable SQL (Core `pin-and-trust-mutex` and `connection-options` validate options only) |
| Native `ConnectWindow` event wiring | Skip | no UI automation |

## 3. Explore to query

| Step | Status | Reason |
|---|---|---|
| Find a table; expand schema | Skip | no disposable SQL |
| Inspect columns/key metadata | Pass | Core `metadata-catalog`, `metadata-keys-indexes-functions` build the catalog SQL only; results Skip: no disposable SQL |
| Generate SELECT into a new, correctly targeted tab | Pass | Core `script-from-object-plan` (plan only); native new-tab step Skip: no UI automation |
| Execute the generated SELECT | Skip | no disposable SQL |
| Ambiguous/quoted names | Pass | Core `sql-identity`, `script-from-object-plan` |
| Restricted visibility / truncated catalogue shown honestly | Pass | Core `oe-truncation-honesty`, `oe-schema-folders`; live denied definitions Skip: no disposable SQL |
| Refresh after a change; cancel an in-flight refresh | Skip | no disposable SQL |

## 4. Author to debug

| Step | Status | Reason |
|---|---|---|
| Open script, find/replace | Pass | Core `find-replace`, `open-deferred-discard`; native find window Skip: no UI automation |
| Large script (2 MB) edit and find | Pass | Core `large-script-settext-and-find` (model operations under 2 s); editor rendering Skip: not measured |
| Save; external edit refused; failed save keeps edits | Pass | Core `d2-external-edit-survives-save`, `save-io-failure`, `save-concurrent-edit`; App catch order checked by `a11y-keyboard-contract-source-scan`; native Save/Save As Skip: no UI automation |
| Quoted/commented GO and source spans | Pass | Core `go-batch-splitter`, `go-parse-source-spans`, `sqlcmd-preflight-rejection` |
| Preflight before any batch executes | Pass | Core `sqlsession-preflight-before-execute`, `go-multi-batch-requires-connection` (fake executor) |
| Run multi-batch selection with a deliberate later error; prior rows remain | Pass | Core `row-failure-preserves-complete-rows`, `execution-outcomes`, `d5-document-relative-rejection`; live run Skip: no disposable SQL |
| Messages/editor location identify the failing batch | Skip | Caret jump and message list not observed; Core `d5-*` covers line mapping only |
| Mid-script `USE` | Skip | no disposable SQL |
| Two competing tab actions | Pass | Core `repeated-run-guard`, `explorer-ownership` |

## 5. Results to file

| Step | Status | Reason |
|---|---|---|
| Copy cells | Skip | Clipboard/UI not exercised; Core `cell-format` covers formatting only |
| Export two distinct result sets; keep decimal precision | Pass | Core `result-export`, `export-from-resultset-types`, `numeric-38-production-path`, `json-duplicate-columns-preserved` (fakes; live decimal(38) Skip: no disposable SQL) |
| NULL distinct from empty string | Pass | Core `result-export` (`csv-null-unquoted-empty`, `csv-empty-string-quoted`, `json-empty-string`) |
| Spreadsheet-safe CSV | Pass | Core `csv-spreadsheet-safe` |
| Cancel/fail an export over an existing destination; old file survives | Pass | Core `safe-file-writer`, `result-export-writer`; native save sheet Skip: no UI automation |

## 6. Design and edit

| Step | Status | Reason |
|---|---|---|
| Preview DDL for a new table with a primary key | Pass | Core `table-design-preview`, `table-design-primary-key` (preview only) |
| Create, then refresh the table | Skip | no disposable SQL |
| Edit one keyed row | Pass | Core `edit-rows-sql`, `edit-rows-batch` (fake transaction); live write Skip: no disposable SQL |
| Force a concurrent update; conflict rolls everything back | Pass | Core `edit-rows-batch`, `edit-rows-binary-collation` (fakes); live conflict Skip: no disposable SQL |
| Unsupported edit refused | Pass | Core `edit-rows-policy` |
| Bounded row reads / huge cell | Pass | Core `query-raw-budget`; live MAX column Skip: no disposable SQL |
| Denied operation | Skip | no disposable SQL |
| Hostile identifier through preview and execute | Skip | no disposable SQL (Core `table-design-preview` covers preview only) |

## 7. Diagnose

| Step | Status | Reason |
|---|---|---|
| Inspect database properties, security/activity | Skip | no disposable SQL |
| Estimated plan for a permitted query | Pass | Core `estimate-plan-order-and-no-dml`, `estimate-plan-format` (fake runner); live SHOWPLAN Skip: no disposable SQL |
| Denied SHOWPLAN | Pass | Core `estimate-plan-permission-denied-no-execution`, `sqlsession-estimate-plan` (fake); live denial Skip: no disposable SQL |
| Estimate never executes or writes | Pass | Core `estimate-plan-order-and-no-dml`, `estimate-plan-set-on-failure-no-execution`, `estimate-plan-rejects-showplan-bypass` (fakes); live write-sentinel Skip: no disposable SQL |
| Unavailable optional feature | Skip | no disposable SQL |
| Diagnostics bundle redaction | Pass | Core `diagnostics-redactor` |

## 8. Recover and exit

| Step | Status | Reason |
|---|---|---|
| Cancel a long query | Pass | Core `observe-one-row-then-cancel`, `observe-one-row-then-watchdog` (fake reader); live long query Skip: no disposable SQL |
| Failure/disconnect without replay | Pass | Core `observe-one-row-then-disconnect`, `observe-one-row-then-transport-loss`, `execute-after-dispose`, `repeated-run-guard` |
| Script edits survive discard prompts and failed save | Pass | Core `open-deferred-discard`, `quit-deferred-discard`, `save-io-failure` |
| Quit with unsaved tabs; cancel quit (native) | Skip | no UI automation |
| Clean up this run's files/resources | Pass | Core tests delete their temporary directories in `finally`; the launch script terminates only the process it started. No SQL resources were created. |

## Open evidence gaps

All Skip rows above. The largest: live disposable SQL for journeys 2–8, VoiceOver and keyboard walk for 1, screenshots
and native dialogs for 1, 4, 5 and 8. Do not cite this file for SSMS parity or for any live behaviour.
