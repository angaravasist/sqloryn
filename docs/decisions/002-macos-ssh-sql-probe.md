# ADR-002 — Mac client SQL probe over owned SSH loopback forward

Accepted for the disposable Mac qualification path only. Owner: SQLoryn maintainer.
Date: 2026-10-31. Extends [ADR-001](001-sqlclient-probe.md); does not select a
desktop shell, credential store or general remote database client.

## Decision and alternatives

Linux x64 Docker on the Mac is unavailable or unsupported for SQL Server evidence.
Keep the local runner unchanged. Add an explicit SSH transport that provisions a
fresh disposable SQL Server on an **already approved** Linux x64 host, returns only
the public server certificate, and runs the existing native Mac SqlClient probe
against a process-owned `127.0.0.1` forward.

| Candidate | Assessment |
|---|---|
| Explicit SSH + local loopback forward | Uses platform OpenSSH; isolates config; keeps SqlClient native on the Mac. Selected. |
| Remote Docker context (`DOCKER_HOST=ssh://…`) | Conflicts with the local-socket safeguard and blurs ownership of forwards/certs. Rejected for this milestone. |
| Split provision/client CLIs | Extra surface for one outcome. Deferred. |
| Tunnel broker / VPN / public SQL publish | New trust and exposure. Requires separate maintainer approval. Not used. |

## Consequences

- Host approval and a maintainer-supplied known_hosts file are prerequisites. No
  host is implied by this ADR. Missing approval leaves Mac SQL evidence unverified.
- SSH is noninteractive (`BatchMode`), config-isolated (`-F /dev/null`), host-key
  pinned to the supplied file, with `ExitOnForwardFailure` and no agent forwarding.
- Remote verbs use a fixed helper and JSON stdin; passwords never appear in remote
  shell command strings. Server private keys are generated remotely and are not
  copied to the Mac.
- Reported remote architecture is checked and known ARM/emulation shapes are
  rejected; architecture strings alone do not prove hardware suitability.
- This is not authorisation to scan for hosts, create paid infrastructure, or
  accept arbitrary existing SQL endpoints.
