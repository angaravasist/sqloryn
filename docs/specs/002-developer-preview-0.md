# SPEC-002 — Developer Preview 0 connected workspace

Status: implemented for local builds; live SQL walkthrough **unverified** (no
approved host). Owner: SQLoryn maintainer. Reviewed: 2026-10-31.
Desktop choice: [ADR-003](../decisions/003-desktop-avalonia.md).

## Outcome

Deliver one runnable macOS development `.app` that supports: connect with SQL
auth + mandatory TLS → browse databases/schemas/tables/columns → edit/open/save
UTF-8 `.sql` → run selection or document in an explicit tab session → view
results/messages → cancel → disconnect. Single-batch execution only (no GO).

## Acceptance (component vs live)

| Area | Component | Live (needs host) |
|---|---|---|
| Connection options / TLS requirements | E1 tests | Wrong cert/password + successful connect |
| Session cancel/dispose/no replay | API + UI wiring | Delayed query cancel + disconnect uncertainty |
| Tab isolation of SQL text | E1 | Two-tab execution destinations |
| Object explorer quoting | E1 identity helpers | Expand tree + restricted login |
| Dirty document protect | UI dialog | Save/discard/cancel on close |
| SSH runner empty args + bootstrap | Python tests | N/A |

## Non-goals

Keychain, Entra, IntelliSense, designers, backup/Agent, telemetry, AI, signing,
public distribution.
