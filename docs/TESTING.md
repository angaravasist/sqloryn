# Build and test

macOS Database Studio preview. Owner: SQLoryn maintainer. Reviewed: 2026-11-03.

## Feature-by-feature TDD and parity evidence

Use the [parallel TDD contract](ssms/PARALLEL-TDD.md) for every parity claim. Record the reference action and configuration, the red/green assertion, the integrated SQLoryn path, negative cases, platform and cleanup. Keep docs-only, Windows-observed, component and native live results distinct.

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

Offline launch timing (process start only; writes `docs/evidence/launch-<arch>.md`):
`bash scripts/measure_app_launch.sh 3`. Offline journey checklist: [JOURNEYS.md](JOURNEYS.md).

Requires the SDK/runtime from `global.json`. Default is a framework-dependent publish for the host RID
(`Sqloryn.csproj` declares `RuntimeIdentifiers` osx-arm64;osx-x64). `SQLORYN_SELF_CONTAINED=1` bundles the runtime;
neither mode has been tested on a Mac without .NET, so no clean-Mac claim exists. Each build writes provenance
(commit, RID, dll hash) to `docs/evidence/package-<arm64|x64>[-self-contained].md`. Set `AVALONIA_TELEMETRY_OPTOUT=1`
for any manual `dotnet build` (the script does). Not a signed installer. See [WALKTHROUGH.md](WALKTHROUGH.md).

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

### Probe alone is not Core acceptance

`SqlClientProbe` proves transport, TLS pinning and the SqlClient/server pairing.
It does **not** exercise `Sqloryn.Core`, so a green probe alone cannot close any
Core finding (date/money materialisation, export fidelity, DDL, edit conflicts,
SHOWPLAN). After the probe passes, `run_sql_probe.py` runs
`tests/Sqloryn.Core.SqlAcceptance` against the **same** disposable instance (a
probe failure stops the run before this step; both local and PROBE-11 SSH-tunnel
modes do this, and neither is relaxed). Stable lines: `PASS`/`SKIP`/`FAIL CORE-*`
and `CORE-ACCEPTANCE COMPLETE pass=N skip=M fail=K`. The runner rejects a run
with no summary, any `fail`, fewer than 5 passes, or a skip other than
`CORE-PLAN-DENIED` (server refused to create a restricted login).

Checks use one uniquely named synthetic schema that is dropped afterwards:
`CORE-MATERIALISE` (date, money, smallmoney: NULL, negative, fractional, extremes),
`CORE-EXPORT` (CSV and JSON from `ResultSetView.FromResultSet`),
`CORE-DESIGN-PK` (`TableDesignSql.PreviewCreate` + execute; second connection
verifies columns and composite PK), `CORE-EDIT-CONFLICT` (two sessions: CI-collation
case change conflicts through the BIN2 predicate, fresh edit commits, two-row
batch rolls back), `CORE-PLAN-INSERT` and `CORE-PLAN-DENIED` (`EstimatePlanAsync`
INSERT writes no row; denied login via `DENY SHOWPLAN`).

Manual run against an instance you already own (disposable, synthetic data only;
encryption is mandatory; set `SQLORYN_ACCEPTANCE_CERT` *or*
`SQLORYN_ACCEPTANCE_TRUST=1`, never both). With none of the variables set it
prints `SKIP CORE-ACCEPTANCE …` and exits 0, which is **not** evidence; a
partial set exits 2:

```sh
export SQLORYN_ACCEPTANCE_SERVER=127.0.0.1 SQLORYN_ACCEPTANCE_PORT=<port> \
  SQLORYN_ACCEPTANCE_USER=<admin> SQLORYN_ACCEPTANCE_DATABASE=<db> \
  SQLORYN_ACCEPTANCE_CERT=/path/to/server.crt
read -rs SQLORYN_ACCEPTANCE_PASSWORD && export SQLORYN_ACCEPTANCE_PASSWORD
dotnet run --project tests/Sqloryn.Core.SqlAcceptance -c Release --no-build
```

The login needs CREATE SCHEMA in the database; `CORE-PLAN-DENIED` also needs
CREATE LOGIN (otherwise it SKIPs with a reason).

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
