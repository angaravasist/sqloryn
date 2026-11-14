# Contributing to SQLoryn

For anything larger than a small fix, open an issue first. Describe the workflow, the SQL Server versions affected and how the change will be tested.

## Coding standards

- Follow [`.editorconfig`](.editorconfig): UTF-8, LF line endings, 4-space indentation for C#, 2 spaces elsewhere.
- [`Directory.Build.props`](Directory.Build.props) enables nullable reference types and treats warnings as errors. Builds must have 0 warnings.
- Dependencies are locked (`packages.lock.json`). Restore with `--locked-mode`. When you change a package version, regenerate every lockfile with `dotnet restore SQLoryn.slnx --force-evaluate`, including projects that use the package only indirectly. Record new packages and their licences in [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md).
- Keep changes small and focused. Add or update tests together with the behaviour they cover.

## Build and test

Run these before opening a pull request (details in [docs/TESTING.md](docs/TESTING.md)):

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore --configuration Release
dotnet run --project tests/SqlClientProbe.Tests --no-build --configuration Release
dotnet run --project tests/Sqloryn.Core.Tests --no-build --configuration Release
python3 -m unittest discover -s tests -p 'test_*.py'
python3 scripts/repo_preflight.py
git diff --check
```

## Pull requests

- Branch from `main` and open a pull request against `main`. Pull requests are squash-merged.
- The required `repo-safeguards` check must pass. Never report a skipped SQL test as passed.
- Say in the description how you tested the change, and on which Mac architecture and SQL Server version.

## Safety rules

- **Never test against a production database.** Use a disposable SQL Server instance with synthetic data. The CI SQL workflow creates and deletes one for each run.
- Anything that writes to a database (DDL, row edits, imports, permissions, backup/restore, Agent jobs) must show the exact SQL. It must ask for confirmation and run in a transaction where SQL Server allows. On failure it must roll back or report an uncertain result honestly; never retry a write silently.
- Treat SQL files, server metadata and user input as untrusted. Quote identifiers and use parameters.
- Never commit or paste passwords, keys, certificates, connection strings, database dumps or screenshots of real data, whether in code, tests, logs, issues or pull requests.
- Encryption is mandatory. Do not add a way to disable TLS validation silently.
- Do not copy Microsoft/SSMS code, artwork or documentation.

Report security issues privately as described in [SECURITY.md](SECURITY.md).
