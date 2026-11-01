# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-11-01. Experimental; no release support.

## Work under review

Branch `codex/developer-preview-0` — Developer Preview 0
([ADR-003](decisions/003-desktop-avalonia.md),
[SPEC-002](specs/002-developer-preview-0.md)). Draft
[PR #6](https://github.com/angaravasist/sqloryn/pull/6).

## Evidence

| Claim | Status |
|---|---|
| Reviewed implementation | `ac03bcc` |
| Retention/completion + Open version correction | `22d3ab81cd7aff37690b544a2e73b4f4880ba16e` (Core + arm64 manual dirty smoke) |
| BatchObserver: cancel / timeout(-2) / transport(-1) / limit+error | Automated Core PASS |
| Open version race + failed read with intervening edit | Automated Core PASS |
| Ordinary dirty Open/Quit in arm64 `.app` | Manual (distinct from Core) — recorded with tip |
| Hosted ubuntu + macos-15 + macos-15-intel | [41301640670](https://github.com/angaravasist/sqloryn/actions/runs/41301640670) |
| Live SQL / provider SqlDataReader decimal(38) | **Unverified** — no host |

## Reviewer launch

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore -c Release
dotnet run --project tests/Sqloryn.Core.Tests -c Release --no-build
bash scripts/build_macos_app.sh
open artifacts/macos/osx-arm64/SQLoryn.app
```

See [WALKTHROUGH.md](WALKTHROUGH.md) for automated vs manual distinction.
