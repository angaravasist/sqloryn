# Reviewer walkthrough — Developer Preview 0

## Launch

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore --configuration Release
dotnet run --project tests/Sqloryn.Core.Tests --no-build --configuration Release
bash scripts/build_macos_app.sh
open "artifacts/macos/$(uname -m | sed 's/arm64/osx-arm64/;s/x86_64/osx-x64/')/SQLoryn.app"
```

## Automated Core tests (not a substitute for UI)

The component suite exercises BatchObserver production paths with the same
`QueryOutcomeFormatter` status strings the app uses for tab status, results and
messages:

- one retained row then UserCancel / Disconnect / Watchdog / provider timeout(-2) /
  transport loss(-1)
- retention limit then value-free drain; later SQL error still observed
  (`DiscardValueGuardReader` rejects discarded GetValue)
- drain deadline expiry → `FinalCompletionUnobserved` (not Succeeded); target
  verification must not upgrade Completion/StopCause
- watchdog expiry during drain while drain deadline still pending →
  `WatchdogTimeout` (linked cancel is not drain attribution)
- timer cause recorded before operation-token cancel callbacks (drain and
  watchdog exercised separately; no post-callback sleep race)
- provider -2 then late Cancel before verification → remains TimedOut (provider)
- deferred Open/Quit with intervening edits; SqlDecimal typed formatting

Those PASS lines are **automated**. They do not prove Avalonia dialogs or focus
behaviour.

## Manual host-independent UI (arm64 `.app`)

Perform these in the launched app (distinct from Core tests above):

1. **Connect dialog** — omit fields; Cancel leaves the workspace unchanged.
2. **Dirty Open** — type into a tab; File → Open; Discard; Cancel the picker.
   Confirm the buffer text and dirty `*` remain.
3. **Dirty Quit** — dirty two tabs; Quit; Discard on the first prompt; Cancel on
   the second. Confirm both tabs still show unsaved text. Quit again and Cancel
   — buffers stay protected.
4. **Close Tab** — dirty a tab; Ctrl+W; Cancel — tab remains dirty.
5. **Run guard** — without a server, Run asks to connect.

Record whether each manual step was performed on the local arm64 build in
STATE.md. Steps 1–5 above remain **unverified** on the current tip except where
STATE notes otherwise. Intel UI smoke remains CI app-publish only unless an
Intel Mac is used.

## Live SQL

**arm64 (observed, partial):** Apple Silicon `.app` connected with SQL auth and a
pinned disposable server certificate over a process-owned loopback SSH tunnel to
an approved host; Object Explorer listed system databases; `SELECT 1 AS test_result`
returned `1`. Lab cleanup verified (tunnel stopped; remote workdir and Docker
label absent; local credentials removed). Host addresses and key paths are not
recorded here — see STATE.md for run-id only.

**Still unverified:** wrong cert/password; USE/target unknown; provider
`SqlDataReader` decimal(38); cancel/disconnect under load; remaining walkthrough
UI checks; **Intel Mac** desktop SQL. Keep arm64 and Intel evidence separate.
