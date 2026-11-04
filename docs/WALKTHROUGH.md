# Reviewer walkthrough — macOS Database Studio (preview)

Requires .NET SDK 10.0.403 (`global.json`). Prefer `~/.dotnet/dotnet` when the system SDK differs.

## Build and open

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore -c Release
dotnet run --project tests/Sqloryn.Core.Tests -c Release --no-build
bash scripts/build_macos_app.sh
open artifacts/macos/osx-arm64/SQLoryn.app   # or osx-x64
```

## Offline workflows (no SQL Server)

1. **Shell** — Menus, toolbar, dark slate/teal theme, original dock icon.
2. **Editor** — New/Open/Save tabs; dirty quit prompts; Find/Replace (Edit menu).
3. **Profiles UI** — Connection → Connect: profile list, Save profile, Remember password (Keychain on macOS). Cancel without connecting.
4. **Settings / Diagnostics** — Tools → Settings; Tools → Diagnostics Bundle (redacted text; save path of your choosing).
5. **Export disabled path** — Export CSV/JSON with no results should explain nothing to export.

## Live SQL workflows (disposable lab only)

Do **not** point at production. Use an isolated SQL Server with synthetic data.

1. **Connect** — SQL auth, encryption mandatory; optional Trust (lab) or cert pin (mutex). Save profile + Remember password.
2. **Object Explorer** — Expand database → schema → Tables/Views/Procedures → columns; Refresh; Script SELECT; Properties pane.
3. **GO batches** — `SELECT 1 AS a; GO SELECT 2 AS b;` → two result grids; Messages tab.
4. **Selection run** — Highlight a subset; F5 runs selection only.
5. **Cancel / timeout** — Long query → Cancel; status distinguishes request vs verified cancel / timeout / unobserved (Core-tested).
6. **Export** — One save dialog per export, one file written (no derived `-2` siblings). CSV = the selected result tab (optional spreadsheet-safe variant prefixes formula-like text with `'`); JSON = columns by ordinal + row arrays, all sets in one document. Writes are staged and replaced atomically; failure/cancel leaves existing files untouched.
7. **Design Table** — Object → Design Table → Preview DDL → Confirm → Apply (expect clear failure text on error).
8. **Edit Top 200** — Requires primary key; conflict when another actor changed the row.

## Explicitly out of scope here

- Windows SSH product connection (removed / not recreated).
- Entra, Agent, AG, SSMS parity claims, signing/notarisation.
