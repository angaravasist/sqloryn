# SPEC-001 — disposable SqlClient conformance probe

Status: E2 verified on the recorded Linux local target; Mac client E2 **unverified**.
See [evidence](../STATE.md). Owner: SQLoryn maintainer. Reviewed: 2026-10-31.

## Outcome and boundary

Establish an executable baseline for SQL Server connectivity before selecting a
desktop framework. This is a developer experiment, not an application or a
supported database client. Related brief capabilities: CAP-002, CAP-013,
CAP-068, CAP-069, CAP-072, CAP-073, CAP-074, CAP-092–095.

Use .NET 10 and the MIT-licensed Microsoft.Data.SqlClient 7.1.1 directly. Keep
SQL Tools Service as a candidate for higher-level tooling. Do not import an
extension distribution or decide the desktop shell in this change.

The probe accepts only loopback TCP, an explicit disposable-instance identity,
and a server certificate supplied by the test runner. Passwords arrive through
stdin. It executes fixed synthetic statements only, validates the instance
before running the suite, prints test identifiers and sanitised version facts,
and never prints connection strings, SQL results or exception messages.

## Acceptance checks

| ID | Observable requirement |
|---|---|
| PROBE-01 | Configuration requires encryption, certificate validation, no pooling and no connection retries; malformed arguments fail before networking. |
| PROBE-02 | Open SQL-authenticated connection; verify the generated server identity and Developer edition; report numeric server version only. |
| PROBE-03 | Correct certificate pin connects; a different certificate is rejected specifically during TLS authentication. No trust bypass exists. |
| PROBE-04 | Incorrect generated password produces an authentication failure, not success or raw diagnostics. |
| PROBE-05 | Consume multiple result sets, including an empty set; preserve NULL, empty string, Unicode, 38-digit decimal, binary and datetimeoffset values. |
| PROBE-06 | Observe informational messages without logging their text; detect SQL errors arriving after a result. |
| PROBE-07 | A command timeout is distinguishable from explicit cancellation. |
| PROBE-08 | Observe an active WAITFOR on a second connection, request cancellation, verify it stopped server-side, and check the original session remains usable with no transaction. |
| PROBE-09 | Read 10,000 synthetic rows sequentially without collecting the result set. This is not a large-result benchmark. |
| PROBE-10 | Local runner refuses non-Linux/non-x64 Docker hosts and remote Docker contexts; requires explicit SQL Server licence acceptance; creates and removes only its own disposable container and temporary material. |
| PROBE-11 | Optional SSH mode targets only a maintainer-approved host with a supplied known_hosts file; isolates SSH config; uses a prepare→provision ownership lifecycle; generates the server private key remotely and returns the public certificate only; keeps effective local `-L` forwarding (no `ClearAllForwardings` on the tunnel); constrains remote Docker to the verified local Unix socket after host `uname` checks; owns/reaps the tunnel on failure; cleanup is independent per resource and reports `run-id` for manual recovery without helper overwrite. Does not weaken PROBE-01–10. |

## Scope of the change

Experiment and executable component tests; local Docker runner; optional SSH
orchestration and remote helper; locked NuGet restore; CI component checks;
architecture decisions, dependency review, test guide, threat boundaries,
feature matrix and current-state handoff. Existing safeguards and branch
protections remain authoritative. No GUI, saved secrets, user SQL files, Entra
flow, schema changes, SQLCMD/GO parser, export, release or hosted product
service is introduced.

## Evidence gate

Component tests establish E1 only. Local Linux E2 requires the entire suite
against the recorded, supported Linux x64 SQL Server image on a local Docker
socket. Native macOS arm64 and x64 client interoperability require actual
Mac-to-approved-server runs using PROBE-11; Linux success and Mac component
jobs do not establish them. Desktop and release evidence remain absent. Failure
or missing infrastructure must remain visible in the feature matrix and handoff.
