# Build and test

Developer Preview 0. Owner: SQLoryn maintainer. Reviewed: 2026-10-31.

## Component checks

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore --configuration Release
dotnet run --project tests/SqlClientProbe.Tests --no-build --configuration Release
dotnet run --project tests/Sqloryn.Core.Tests --no-build --configuration Release
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 scripts/repo_preflight.py
git diff --check
```

## macOS app package

```sh
bash scripts/build_macos_app.sh
open artifacts/macos/osx-arm64/SQLoryn.app   # arm64
# open artifacts/macos/osx-x64/SQLoryn.app   # Intel
```

Requires the SDK/runtime from `global.json`. Framework-dependent publish; not a
signed installer. See [WALKTHROUGH.md](WALKTHROUGH.md).

## SQL acceptance

- Probe harness: [SPEC-001](specs/001-sqlclient-probe.md) via `run_sql_probe.py`.
- App walkthrough: [SPEC-002](specs/002-developer-preview-0.md) — needs an
  approved disposable host; not authorised by documentation alone.

Local disposable instance (Linux x64 Docker socket):

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build SQLoryn.slnx --configuration Release
python3 scripts/run_sql_probe.py --accept-sql-server-license
```

Optional PROBE-11 remote mode (maintainer-approved host only). Requires an
existing known_hosts file with the verified host key. Optional
`--ssh-identity-file` selects a dedicated private key for every SSH/SCP/tunnel
call while keeping `StrictHostKeyChecking=yes` and `IdentitiesOnly=yes`. Omit
the identity flag to keep OpenSSH’s default isolated identity search. Do not
commit hostnames, addresses, fingerprints or key paths.

```sh
python3 scripts/run_sql_probe.py --accept-sql-server-license \
  --ssh-host 'user@approved-host' \
  --ssh-known-hosts /path/to/verified_known_hosts \
  --ssh-identity-file /path/to/dedicated_private_key
```

Empty `--ssh-host` / `--ssh-known-hosts` fail before Docker/SSH. Incomplete
remote bootstrap never executes `helper.py`.
