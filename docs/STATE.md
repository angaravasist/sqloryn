# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-11-01. Experimental; no release support.

## Work under review

Branch `codex/developer-preview-0` — Developer Preview 0 Avalonia app + Core
session ([ADR-003](decisions/003-desktop-avalonia.md),
[SPEC-002](specs/002-developer-preview-0.md)). Draft
[PR #6](https://github.com/angaravasist/sqloryn/pull/6).

## Merged baseline

- `origin/main` at `006612a` (PROBE-11). Linux regression:
  [41194422292](https://github.com/angaravasist/sqloryn/actions/runs/41194422292).

## Evidence

| Claim | Status |
|---|---|
| Hosted checks for fidelity tip | [41300470636](https://github.com/angaravasist/sqloryn/actions/runs/41300470636) (ubuntu + macos-15 + macos-15-intel) |
| Reviewed implementation | `de4cce0` |
| Fidelity/lifecycle correction (this delivery) | `ac03bcc71f7c3172b970e7fb107f2562806b9b7b` (arm64 component + .app dirty walkthrough) |
| SqlDecimal typed path (38-digit probe literal) | Component pass; **live SqlDataReader on SQL Server unverified** |
| Mid-row budget + `RenderForDisplay` truncation marker | Component pass |
| One-row-then-throw → row + error + partial | Component pass |
| Deferred Open/Quit dirty protection | Component pass + local macOS `.app` dirty-sequence walkthrough |
| Target verification separated from SQL outcome | Component pass; live USE/refresh **unverified** |
| Python runner suite | Pass (40 tests) |
| Native Mac live SQL walkthrough | **Unverified** — no approved host |

## Reviewer can do today (no SQL host)

```sh
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore -c Release
dotnet run --project tests/Sqloryn.Core.Tests -c Release --no-build
bash scripts/build_macos_app.sh
open artifacts/macos/osx-arm64/SQLoryn.app   # or osx-x64 on Intel
```

Follow [WALKTHROUGH.md](WALKTHROUGH.md) dirty Open/Quit sequences.

## Next

Approved disposable SQL Server for live fidelity and target-verification cases;
keep arm64 and Intel evidence separate.
