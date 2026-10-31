# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-10-31. Experimental; no release support.

## Work under review

Branch `codex/developer-preview-0` — Developer Preview 0 Avalonia app + Core
session ([ADR-003](decisions/003-desktop-avalonia.md),
[SPEC-002](specs/002-developer-preview-0.md)). Draft
[PR #6](https://github.com/angaravasist/sqloryn/pull/6).

## Merged baseline

- `origin/main` includes SqlClient probe + PROBE-11 tools ([PR #5](https://github.com/angaravasist/sqloryn/pull/5) merged as `006612a`).
- Linux SQL regression on that line:
  [41194422292](https://github.com/angaravasist/sqloryn/actions/runs/41194422292).

## Evidence

| Claim | Status |
|---|---|
| Probe Linux SQL E2 (historical / regression) | Verified on recorded runs; see prior STATE history |
| Prior Preview 0 CI Mac arm64+Intel app publish | [41219130069](https://github.com/angaravasist/sqloryn/actions/runs/41219130069) at `3b37d5b` |
| Workspace correction commit (this delivery) | `de4cce00374abf9762a54f7b2b65c095267659a3` (arm64 local build+walkthrough) |
| Sqloryn.Core E1 (ownership, outcomes, lifecycle, 38-digit, budget, RunGuard) | Local pass |
| Python runner suite | Local pass (40 tests) |
| Avalonia `.app` build + host-independent UI walkthrough (arm64) | Local (this machine) |
| CI Mac arm64 / Intel app publish for correction tip | [41234937796](https://github.com/angaravasist/sqloryn/actions/runs/41234937796) (ubuntu + macos-15 + macos-15-intel pass) |
| Native Mac arm64/x64 → live SQL walkthrough | **Unverified** — no approved host |
| VoiceOver device pass | **Unverified** — automation names present only |

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

Follow [WALKTHROUGH.md](WALKTHROUGH.md) host-independent steps. Live connect
awaits maintainer host approval.

## Next

Approved disposable SQL Server for SPEC-002 live walkthrough; keep arm64 and
Intel evidence separate. Do not treat SSH probe success as desktop acceptance.
