# Current trust boundaries and risks

Experimental probe. Owner: SQLoryn maintainer. Reviewed: 2026-10-31.
Risk IDs retain the brief's references; severity is potential impact. No supported
users/releases exist. This is not an independent security audit.

## Data, network and retention

Restore contacts nuget.org for packages/advisories; CI also uses GitHub and
Microsoft SDK/image endpoints. The running probe contacts only loopback SQL TCP;
the server is on an internal Docker network. No AI/telemetry provider is added.
CLI telemetry is disabled in CI/documented commands; no external logger or
diagnostic listener is enabled.

Generated certificates/keys and TLS config occupy an OS temporary directory with
a 0700 parent. The server key is readable in the mounted child directory by SQL
Server's non-root UID. Cleanup removes temporary material, container/network and
anonymous container volumes. SDK/NuGet/image caches remain; these contain tools,
not query results. Server logs/data live only in its disposable container.

The temporary password exists in memory, Docker create's environment/container
configuration and .NET's stdin/managed driver options. The local user and Docker
administrator can read it; managed strings are not guaranteed to be zeroed.
This is not a Keychain or production credential design. Never supply real secrets.

## Controls and remaining evidence

| Risk / potential impact | Current control | Remaining gate |
|---|---|---|
| R-001, R-021 / high: secret exposure | No secret arguments; bounded stdin; fixed safe diagnostics; no dump upload | Same-user/admin/memory exposure remains; Keychain and crash design before an app |
| R-002 / critical: server impersonation | Mandatory TLS, exact certificate pin, no trust bypass | Normal CA/name/expiry checks and Mac connections remain unverified |
| R-003, R-022 / critical: wrong target/production | New instance only, loopback, generated identity, edition/version check | Sentinel prevents accidents; it is not authorisation; never target an existing tunnel |
| R-004 / critical: replay | No connection retries, pooling or query retry loop | Lost connections during writes/transactions not tested; no persistent writes in probe |
| R-006, R-007 / high: fidelity/memory | Typed assertions, sequential 10,000-row read | General cells, rendering/export, backpressure and benchmarks absent |
| R-016 / high: false cancellation | Observe active request, cancellation, absent request, usable original session | WAITFOR with test admin only; partitions/transactions open |
| R-009, R-019 / high: dependency/licence | Locked packages, fail-closed audit, SHA-pinned Actions/image, native SNI excluded | Restore obtains separately licensed SNI; inspect all actual artifacts/notices before release |
| R-010, R-015 / high: false claims/stale docs | Separate candidate catalogue, matrix and linked evidence | Keep state/tests/docs updated together; docs are not proof |
| R-011, R-024 / high: platforms | Reject server emulation; both Mac component architectures in CI | Mac-to-server testing and signed installers/update evidence absent |
| R-013 / critical: PR exfiltration | Read-only token, pinned actions, no protected secrets/self-hosted runner; SQL job explicit | Review workflow diffs; temporary SQL admin password is intentionally available to its own job |
| R-020 / high: no independent review | PR required; agent does not merge/release | Single maintainer cannot supply independent approval; seek review before distribution |

Other risks are **unimplemented/unassessed**: destructive GUI (R-005), updater
(R-008), Windows/BI management (R-012), optional AI (R-014), concurrent editing
(R-017), target capability confusion (R-018) and accessibility (R-023). Their
absence does not settle the risk when introducing those surfaces.

Release remains blocked by absent app/desktop evidence and unresolved
distribution, credential and independent-review gates. Signing, publishing,
protected-setting changes and production access require the maintainer.
