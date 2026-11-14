# ADR-002: Remote disposable SQL Server over SSH for Mac tests

Date: 2026-10-31. This applies to the test runner only; it is not a connection feature of the app.

## Decision

SQL Server's Linux x64 container cannot run reliably on Apple Silicon. For Mac client tests, `scripts/run_sql_probe.py` can therefore create the disposable server on a Linux x64 host the developer controls. It reaches the host over SSH. Only the server's public certificate comes back to the Mac, and the native Mac SqlClient connects through a `127.0.0.1` port forward owned by the runner.

| Option | Assessment |
|---|---|
| Explicit SSH plus a local loopback forward | Uses the system OpenSSH with isolated configuration, and keeps SqlClient native on the Mac. **Chosen.** |
| Remote Docker context (`DOCKER_HOST=ssh://…`) | Conflicts with the local-socket safeguard and blurs ownership of forwards and certificates. |
| Tunnel broker, VPN or publicly published SQL port | Adds new trust and exposure. Not used. |

## Consequences

- The developer supplies the host and a known_hosts file containing its verified key.
- SSH runs non-interactively (`BatchMode`), ignores user configuration (`-F /dev/null`), pins the host key and uses `ExitOnForwardFailure`. It never forwards the SSH agent.
- Remote actions go through a fixed helper script (`prepare`, `provision`, `cleanup`) with JSON on stdin, so passwords never appear in remote command lines. The server's private key is generated on the remote host and never copied to the Mac.
- The reported remote architecture is checked, and known ARM or emulated engines are rejected.
- Cleanup removes only resources named with the run's unique id.
