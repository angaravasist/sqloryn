# Current trust boundaries and risks

Experimental probe. Owner: SQLoryn maintainer. Reviewed: 2026-10-31.
Risk IDs retain the brief's references; severity is potential impact. No supported
users/releases exist. This is not an independent security audit.

## Data, network and retention

Restore contacts nuget.org for packages/advisories; CI also uses GitHub and
Microsoft SDK/image endpoints. The running probe contacts only loopback SQL TCP. In local mode the server is on
a dedicated Docker bridge with the SQL port published only on loopback. In SSH
mode the Mac opens an owned local loopback forward to the remote host's
loopback-published port; SQL is not published publicly. The bridge permits
outbound traffic: this is a disposable synthetic test environment, not an egress
sandbox. No AI/telemetry provider is added. CLI telemetry is disabled in
CI/documented commands; no external logger or diagnostic listener is enabled.

Local mode keeps generated certificates/keys in an OS temporary directory with a
0700 parent. SSH mode generates the server private key on the remote host only
and returns the public certificate PEM through the authenticated channel for
pinning; wrong-pin certificates are local. Cleanup removes temporary material,
owned container/network resources and owned remote work directories. SDK/NuGet/
image caches remain; these contain tools, not query results. Server logs/data
live only in the disposable container. Interrupted SSH can leave remote
resources; recovery is ownership-scoped to the exact run name—never broad prune.

The temporary password exists in memory, Docker create's environment/container
configuration and .NET's stdin/managed driver options. The local user and Docker
administrator can read it; managed strings are not guaranteed to be zeroed.
This is not a Keychain or production credential design. Never supply real secrets.

## Controls and remaining evidence

| Risk / potential impact | Current control | Remaining gate |
|---|---|---|
| R-001, R-021 / high: secret exposure | No secret arguments; bounded stdin; fixed safe diagnostics; no dump upload | Same-user/admin/memory exposure remains; Keychain and crash design before an app |
| R-002 / critical: server impersonation | Mandatory TLS, exact certificate pin, no trust bypass; SSH host-key file required | Normal CA/name/expiry checks and Mac SQL runs remain unverified without an approved host |
| R-003, R-022 / critical: wrong target/production | New instance only, loopback, generated identity, edition/version check; SSH config isolated; no arbitrary endpoints | Sentinel/host-key prevent accidents; they are not authorisation; never attach to an existing tunnel |
| R-SSH / high: remote command/tunnel misuse | Fixed remote helper, prepare ownership, JSON stdin for secrets, token-safe argv, tunnel options without ClearAllForwardings so `-L` remains effective, local Docker socket binding, independent cleanup + run-id recovery | Live SSH behaviour unproven until an approved host exists; orphaned remote resources on hard kill/network loss |
| R-004 / critical: replay | No connection retries, pooling or query retry loop | Lost connections during writes/transactions not tested; no persistent writes in probe |
| R-006, R-007 / high: fidelity/memory | Typed assertions, sequential 10,000-row read | General cells, rendering/export, backpressure and benchmarks absent |
| R-016 / high: false cancellation | Observe active request, cancellation, absent request, usable original session | WAITFOR with test admin only; partitions/transactions open |
| R-009, R-019 / high: dependency/licence | Locked packages, fail-closed audit, SHA-pinned Actions/image, native SNI excluded | Restore obtains separately licensed SNI; inspect all actual artifacts/notices before release |
| R-010, R-015 / high: false claims/stale docs | Separate candidate catalogue, matrix and linked evidence | Keep state/tests/docs updated together; docs are not proof |
| R-011, R-024 / high: platforms | Reject server emulation; both Mac component architectures in CI; SSH path rejects known ARM engine reports | Native Mac-to-server SQL evidence absent; reported arch ≠ hardware proof; signed installers absent |
| R-013 / critical: PR exfiltration | Read-only token, pinned actions, no protected secrets/self-hosted runner; SQL job explicit | Review workflow diffs; temporary SQL admin password is intentionally available to its own job |
| R-020 / high: no independent review | PR required; agent does not merge/release | Single maintainer cannot supply independent approval; seek review before distribution |

Other risks are **unimplemented/unassessed**: destructive GUI (R-005), updater
(R-008), Windows/BI management (R-012), optional AI (R-014), concurrent editing
(R-017), target capability confusion (R-018) and accessibility (R-023). Their
absence does not settle the risk when introducing those surfaces.

Release remains blocked by absent app/desktop evidence and unresolved
distribution, credential and independent-review gates. Signing, publishing,
protected-setting changes and production access require the maintainer.
