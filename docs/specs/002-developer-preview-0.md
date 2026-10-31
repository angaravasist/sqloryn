# SPEC-002 — Developer Preview 0 connected workspace

Status: implemented for local builds and component tests; live SQL walkthrough
**unverified** (no approved host). Owner: SQLoryn maintainer. Reviewed: 2026-10-31.
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
| Async ownership (tab/session/document/database); stale rejection | Delayed metadata + binding tests | Two DBs / identical names under load |
| Explorer `database.schema.object` quoting; browse ≠ insert SQL | E1 Qualify3 + BuildSelectScript | Expand tree + restricted login |
| Run guard (no second Run cancel/queue); cancel requested / client ended / server verified; timeout -2 ≠ disconnect; no replay | E1 outcomes + RunGuard | Delayed cancel + disconnect uncertainty |
| Dirty Open/Quit/Close-tab; sync native close cancel; save I/O + concurrent edit | E1 DocumentLifecycle + ScriptDocument | Manual save/discard on live session |
| 38-digit decimal (probe literal), bounded text/binary, aggregate budget, partial late error | E1 ResultMaterializer | Full fidelity against SQL Server |
| Keyboard / menu tree expansion path | Wired in UI (host-independent) | With live metadata |
| Tab isolation of SQL text / late UI apply | E1 + dispose | Two-tab execution destinations |
| SSH runner empty args + bootstrap | Python tests | N/A |

## Non-goals

Keychain, Entra, IntelliSense, designers, backup/Agent, telemetry, AI, signing,
public distribution.
