# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-11-03. Experimental; no release support.

## Work under review

Branch `codex/developer-preview-0` — Developer Preview 0
([ADR-003](decisions/003-desktop-avalonia.md),
[SPEC-002](specs/002-developer-preview-0.md)). Draft
[PR #6](https://github.com/angaravasist/sqloryn/pull/6).

## Evidence

| Claim | Status |
|---|---|
| Reviewed implementation (prior tip) | `22d3ab81cd7aff37690b544a2e73b4f4880ba16e` |
| StopCause / unobserved / value-free drain correction | `d720b17966805fe8f3f86d8494f70d3b9bbb7f30` |
| Timer attribution: independent watchdog vs drain evidence; late Cancel preserves timeout | `ad288f88bbc3ae0ae641249eab503b8dae4159c0` |
| Record timer cause before signalling operation token (drain + watchdog) | `8852059bc7eaccb5fc6f4138d2259fac6220cba0` (Core component suite PASS) |
| Cause visible inside operation cancel callback (drain + watchdog) + UI status | Automated Core PASS |
| Watchdog during drain (deadline still pending) → `WatchdogTimeout` on UI | Automated Core PASS |
| Provider -2 then late Cancel → stays `TimedOut (provider)` | Automated Core PASS |
| Distinct `FinalCompletionUnobserved`; shared `QueryOutcomeFormatter` | Automated Core PASS |
| Drain without discarded GetValue; later SQL error under drain deadline | Automated Core PASS |
| Target verification never upgrades SQL Completion/StopCause | Automated Core PASS |
| Open version race + failed read with intervening edit | Automated Core PASS |
| Ordinary dirty Open/Quit / remaining host-independent walkthrough steps | **Unverified** on this tip (prior dirty Open/Quit only) |
| Hosted ubuntu + macos-15 + macos-15-intel | [41416613488](https://github.com/angaravasist/sqloryn/actions/runs/41416613488) |
| Optional `--ssh-identity-file` for PROBE-11 (command/SCP/tunnel/cleanup) | `695061aa8eca91ce9e99eb69d990c2b03af92eb8` (runner unit tests PASS) |
| Mac **arm64** desktop: connect (SQL auth + pinned cert) → OE system DBs → `SELECT 1 AS test_result` → `1` | **Observed** (Apple Silicon `.app`, disposable loopback tunnel to approved host) |
| Lab cleanup for that desktop run (owned tunnel stop; remote workdir gone; Docker label absent; local secrets wiped; run-id preserved) | **Verified** (`cleanup_lab.sh` PASS; run-id `sqloryn-probe-9ccabb4ecf0a23ab`) |
| Full PROBE-11 automated suite / SqlDataReader decimal(38) / cancel-disconnect live matrix | **Unverified** |
| Intel Mac desktop SQL / Intel walkthrough | **Unverified** |

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
