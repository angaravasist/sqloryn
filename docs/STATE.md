# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-10-31. Experimental; no release support.

## Work under review

Branch `codex/developer-preview-0` — Developer Preview 0 Avalonia app + Core
session ([ADR-003](decisions/003-desktop-avalonia.md),
[SPEC-002](specs/002-developer-preview-0.md)). Draft PR carries the launchable
`.app`, component tests, SSH runner corrections and docs.

## Merged baseline

- `origin/main` includes SqlClient probe + PROBE-11 tools ([PR #5](https://github.com/angaravasist/sqloryn/pull/5) merged as `006612a`).
- Linux SQL regression on that line:
  [41194422292](https://github.com/angaravasist/sqloryn/actions/runs/41194422292).

## Evidence

| Claim | Status |
|---|---|
| Probe Linux SQL E2 (historical / regression) | Verified on recorded runs; see prior STATE history |
| Mac arm64/x64 component CI (probe) | Verified on PR #5 runs |
| Avalonia app builds + launches on this Mac (arm64) | Locally observed: `scripts/build_macos_app.sh`; process stayed up until killed |
| Sqloryn.Core E1 assertions | Local pass (TLS policy, cells/bounds, GO policy, tab SQL isolation, dispose) |
| Python runner tests including empty SSH args / bootstrap | Local pass (40 tests) |
| Native Mac arm64 app → live SQL walkthrough | **Unverified** — no approved host |
| Native Mac x64 app → live SQL | **Unverified** — needs Intel Mac + host |
| VoiceOver device pass | **Unverified** — automation names present only |

## Reviewer can do today (no SQL host)

```sh
export PATH="$HOME/.dotnet:$PATH"   # or install SDK 10.0.403
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore -c Release
dotnet run --project tests/Sqloryn.Core.Tests -c Release --no-build
bash scripts/build_macos_app.sh
open artifacts/macos/osx-arm64/SQLoryn.app   # or osx-x64 on Intel
```

Walk the UI: Connect dialog (validation), New/Open/Save, tabs, Run mode label,
Escape cancel (no server), Quit dirty prompt. Live connect awaits maintainer
host approval.

## Next

Approved Linux x64 SQL host for SPEC-002 walkthrough on arm64, then Intel Mac
client evidence. Do not treat SSH probe success as desktop acceptance.
