# Building and testing

All commands run from the repository root and need the .NET SDK pinned in `global.json`.

## Component tests (no SQL Server)

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore --configuration Release
dotnet run --project tests/SqlClientProbe.Tests --no-build --configuration Release
dotnet run --project tests/Sqloryn.Core.Tests --no-build --configuration Release
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 scripts/repo_preflight.py
git diff --check
```

CI runs the same steps on Linux, macOS arm64 and macOS Intel for every pull request. The macOS jobs also build the app bundle.

## macOS app

```sh
bash scripts/build_macos_app.sh                      # framework-dependent, host architecture
SQLORYN_SELF_CONTAINED=1 bash scripts/build_macos_app.sh   # bundles the .NET runtime
open artifacts/macos/osx-arm64/SQLoryn.app           # osx-x64 on Intel
bash scripts/measure_app_launch.sh 3                 # optional: time from `open` to process start
```

The build script writes a provenance file (commit, runtime identifier, file hashes) to `artifacts/macos/provenance-<arch>.md`. The launch script writes `artifacts/macos/launch-<arch>.md`. Both are git-ignored.

## SQL Server acceptance (disposable server)

Live tests never use an existing or production server. The runner creates a new SQL Server 2022 Developer container on Linux x64 Docker, with generated credentials and synthetic data. It runs the checks and then deletes everything.

### In CI (recommended)

The `sql-integration` job runs only when started by hand, because it requires accepting the SQL Server Developer licence for test use:

```sh
gh workflow run repository-safeguards.yml --ref <branch> -f accept_sql_server_license=true
```

A passing run prints `CORE-ACCEPTANCE COMPLETE pass=12 skip=0 fail=0`, followed by `PASS cleanup: disposable container/network and temporary files removed.` On ordinary pull-request runs this job shows as skipped.

### Locally (Linux x64 with Docker)

```sh
dotnet build SQLoryn.slnx --configuration Release
python3 scripts/run_sql_probe.py --accept-sql-server-license
```

The runner first runs `SqlClientProbe`, which checks TLS pinning, authentication, result types, timeouts and cancellation. It then runs `tests/Sqloryn.Core.SqlAcceptance` against the same server. A passing probe on its own is not a passing acceptance run. Each Core check reports a `PASS`, `SKIP` or `FAIL` line:

| Check | What it verifies |
|---|---|
| `CORE-MATERIALISE` | date, money and smallmoney values, including NULL, negative and extreme values |
| `CORE-EXPORT` | CSV and JSON export keep those values |
| `CORE-DESIGN-PK` | New Table with a composite primary key, verified from a second connection |
| `CORE-EDIT-CONFLICT` | A case-only concurrent change is detected; a fresh edit commits; a failed batch rolls back |
| `CORE-PLAN-INSERT` | The estimated plan for an INSERT inserts no row |
| `CORE-PLAN-DENIED` | An estimate without SHOWPLAN permission is denied and inserts no row |
| `CORE-TIME-GUID` | time and uniqueidentifier values |
| `CORE-DESIGN-APPLY` | Transactional create/alter, rollback on failure, stale-design refusal |
| `CORE-EDIT-INSERT-DELETE` | Row insert/delete with conflict rollback |
| `CORE-CSV-IMPORT` | Create and Append store exact values |
| `CORE-SECURITY` | A GRANT preview changes no permissions |
| `CORE-BACKUP` | A COPY_ONLY backup is recorded in msdb |

The runner fails if any check fails, if the summary line is missing, or if a required check is skipped. Only `CORE-PLAN-DENIED`, `CORE-SECURITY` and `CORE-BACKUP` may skip, when the server cannot support them.

### Against a disposable server you already run

```sh
export SQLORYN_ACCEPTANCE_SERVER=127.0.0.1 SQLORYN_ACCEPTANCE_PORT=<port> \
  SQLORYN_ACCEPTANCE_USER=<admin> SQLORYN_ACCEPTANCE_DATABASE=<db> \
  SQLORYN_ACCEPTANCE_CERT=/path/to/server.crt     # or SQLORYN_ACCEPTANCE_TRUST=1, not both
read -rs SQLORYN_ACCEPTANCE_PASSWORD && export SQLORYN_ACCEPTANCE_PASSWORD
dotnet run --project tests/Sqloryn.Core.SqlAcceptance -c Release --no-build
```

The checks create one uniquely named schema and drop it afterwards. The login needs CREATE SCHEMA, and CREATE LOGIN for `CORE-PLAN-DENIED`. With no variables set the program prints `SKIP CORE-ACCEPTANCE` and exits 0. That is not a test result.

### Remote mode over SSH (optional)

On a Mac without Linux x64 Docker, the runner can create the disposable server on a Linux x64 host you control. It reaches the host over SSH with a pinned host key and uses a local loopback tunnel:

```sh
python3 scripts/run_sql_probe.py --accept-sql-server-license \
  --ssh-host 'user@host' \
  --ssh-known-hosts /path/to/verified_known_hosts \
  --ssh-identity-file /path/to/dedicated_private_key   # optional
```

Do not commit host names, addresses, fingerprints or key paths.
