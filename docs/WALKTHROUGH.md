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

The component suite exercises BatchObserver production paths (one row then
cancel / timeout / transport loss; retention limit then later error), deferred
Open/Quit with intervening edits, and SqlDecimal typed formatting. Those PASS
lines are **automated**. They do not prove Avalonia dialogs or focus behaviour.

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
STATE.md. Intel UI smoke remains CI app-publish only unless an Intel Mac is used.

## Live SQL (pending approved host)

Wrong cert/password; connect; USE/target unknown; provider SqlDataReader
decimal(38); cancel/disconnect. Keep arm64 and Intel evidence separate.
