# SPEC-002 — Developer Preview 0 connected workspace

Status: implemented for local builds and component tests; live SQL walkthrough
**unverified** (no approved host). Owner: SQLoryn maintainer. Reviewed: 2026-11-01.
Desktop choice: [ADR-003](../decisions/003-desktop-avalonia.md).

## Outcome

Deliver one runnable macOS development `.app` that supports: connect with SQL
auth + mandatory TLS → browse databases/schemas/tables/columns → edit/open/save
UTF-8 `.sql` → run selection or document in an explicit tab session → view
results/messages → cancel → disconnect. Single-batch execution only (no GO).

## Acceptance (demonstrated vs live)

| Area | Demonstrated (component / host-independent UI) | Live (needs approved host) |
|---|---|---|
| Connection options / TLS requirements | E1 validation tests | Wrong cert/password + successful connect |
| Async ownership; stale OE rejection; Qualify3 | E1 | Two DBs / identical names under load |
| SqlDecimal via typed reader path (38-digit probe literal); no `System.Decimal` | E1 `ISqlTypedCellSource` | Provider `SqlDataReader.GetSqlDecimal` on SQL Server |
| Output budget mid-row; omit incomplete row; UI truncation marker | E1 + `RenderForDisplay` | Wide live result sets |
| One-row-then-throw preserves complete row + error + partial | E1 fake reader | Late SQL error after rows |
| Deferred Open/Quit discard; cancel picker / later Cancel keeps dirty | E1 DocumentLifecycle + macOS manual dirty sequences | — |
| SQL outcome vs target verification; block run when target unknown; no replay | E1 `WithTargetVerification` | USE+error; write+failed DB_NAME |
| Run guard; cancel kinds; timeout -2 ≠ break | E1 | Delayed cancel / disconnect |
| Keyboard / menu expand ≠ Insert SELECT | UI wiring | Live metadata |
| SSH runner empty args + bootstrap | Python tests | N/A |

## Non-goals

Keychain, Entra, IntelliSense, designers, backup/Agent, telemetry, AI, signing,
public distribution.
