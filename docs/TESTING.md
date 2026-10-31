# Build and test

Developer experiment. Owner: SQLoryn maintainer. Reviewed: 2026-10-31.
Acceptance contract: [SPEC-001](specs/001-sqlclient-probe.md).

## Component checks — no database, E1

Install the SDK in `global.json` (10.0.403 or a later patch of that feature band)
and Python 3. Use the repository NuGet config; never add credentials to it.
From the repository root:

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore --configuration Release
dotnet run --project tests/SqlClientProbe.Tests --no-build --configuration Release
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 scripts/repo_preflight.py
git diff --check
```

The .NET suite is executable assertions, not a `dotnet test` project. Nonzero
exit means failure. It checks rejected inputs, secure options, password bounds
and diagnostic privacy. Python tests cover licence/platform/cleanup/SSH boundary
checks without Docker or a live remote host. Repository preflight checks paths,
not secret contents.

Restore audits direct/transitive packages; vulnerabilities and audit failures are
errors. To update packages, review licences/advisories, restore without locked
mode, inspect both lockfiles, update [the inventory](DEPENDENCIES.md) and rerun.
Do not suppress warnings to unblock a build.

## Actual SQL acceptance — local Linux E2 (PROBE-10)

Use a **local Linux x64 Docker engine**. ARM emulation and remote Docker contexts
are rejected. Provide Docker, OpenSSL, .NET and the container's 3 GiB memory.
The runner downloads a digest-pinned SQL Server 2022 CU27 GDR1 image. SQL Server
Developer is proprietary development/test software: explicit maintainer approval
of its terms is required. After Release build and that approval:

```sh
python3 scripts/run_sql_probe.py --accept-sql-server-license
```

No existing endpoint/credential is accepted. The runner generates a hostname,
password and two certificates, creates a dedicated bridge network and loopback-only
port, runs fixed synthetic SQL, and removes its own labelled resources. It
mounts no host database files and retains no deliberate database volume.
Cleanup failure fails the run. Never upload raw SQL logs/container configuration.

## Actual SQL acceptance — Mac client via SSH (PROBE-11, blocked without host)

Requires maintainer approval of a Linux x64 SSH host and a known_hosts file that
already contains that host key. Do not scan for hosts or disable host-key checks.
Preconfigure public-key authentication; the runner is noninteractive and will not
prompt for passwords or create credentials.

After Release build, licence acceptance and host approval:

```sh
python3 scripts/run_sql_probe.py --accept-sql-server-license \
  --ssh-host 'user@approved.host.example' \
  --ssh-known-hosts /path/to/approved_known_hosts
```

The runner uploads `scripts/sql_probe_remote_helper.py`, verifies the remote
Docker engine reports Linux x64 (not hardware proof), generates the server key
on the remote host, returns only the public certificate, opens an owned local
`127.0.0.1` forward with `ExitOnForwardFailure`, and runs the same native
SqlClient suite against the forward. Wrong-pin certificates are generated locally.
Server private keys are not copied to the Mac.

Record client OS, `Client:` process architecture line (must be native arm64 on
arm64 Mac and native x64 on Intel Mac; Rosetta/cross-compile does not qualify),
SDK/runtime/driver, server version/edition/image digest, transport and outcomes
in [STATE.md](STATE.md). Until an approved host exists, Mac SQL evidence stays
**unverified**.

### Remote cleanup recovery

If the Mac process dies, inspect only remote resources whose label value equals
the exact run name `sqloryn-probe-<16 hex>` (`org.sqloryn.disposable-probe`) and
the matching `/tmp/sqloryn-probe-<16 hex>` directory after verifying the `OWNER`
marker. Remove those specific resources. Never use broad Docker prune/deletion.
Unreachable remote cleanup is a failed run.

## Shared acceptance notes

Checks cover correct/incorrect certificate pins, invalid login, multiple/empty
result sets, data fidelity, notices, late SQL errors, timeout, observed server
cancellation/session reuse and 10,000 sequential rows. WAITFOR cancellation does
not prove rollback of arbitrary transactions or cancellation after network loss.
10,000 scalar rows is a smoke test, not a large-cell or performance benchmark.
Ordinary CA/name/expiry TLS validation remains unverified under exact pinning.

## CI and evidence

`Repository safeguards` runs components on Linux x64 and Mac arm64/x64. Required
`repo-safeguards` aggregates those results and the original path/whitespace checks,
including when a component job fails. SQL integration requires manual dispatch
with `accept_sql_server_license=true`; a requested run must pass. Unrequested SQL
checks are **skipped, not verified**. PRs cannot implicitly accept the licence.
Elaborate CI networking for Mac-to-remote SQL is not required for this milestone;
manual PROBE-11 execution is acceptable once a host is approved.

Record exact commit/run URL, client OS/architecture/runtime, server build/edition,
image digest, auth/TLS mode, test IDs and exclusions in [STATE.md](STATE.md).
Never commit credentials, certificates, raw results or dumps.

E0 = expectation/research; E1 = component; E2 = isolated actual SQL Server;
E3 = actual Mac desktop workflow; E4 = signed release qualification. A Mac build
is not a Mac connection test. No E3/E4 evidence exists. Native Mac E2 is pending
approved-host runs.
