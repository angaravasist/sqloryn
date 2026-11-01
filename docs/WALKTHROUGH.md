# Reviewer walkthrough — Developer Preview 0

## Launch

Install .NET SDK matching `global.json` (10.0.403 band). From the repo root
(paths with spaces are OK):

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore --configuration Release
dotnet run --project tests/Sqloryn.Core.Tests --no-build --configuration Release
bash scripts/build_macos_app.sh
open "artifacts/macos/$(uname -m | sed 's/arm64/osx-arm64/;s/x86_64/osx-x64/')/SQLoryn.app"
```

## Host-independent checks (exercised)

1. **Connect dialog** — validation errors; Cancel leaves the workspace unchanged.
2. **Tabs** — New Query; distinct SQL per tab; Run mode document vs selection;
   Close Tab (Ctrl+W) with dirty prompt.
3. **Dirty Open (deferred discard)** — edit a tab; File → Open; choose Discard;
   Cancel the file picker. The buffer text and dirty `*` must remain. Repeat
   Open → Discard → pick a missing/unreadable path if available: unsaved text
   must remain.
4. **Dirty Quit (deferred discard)** — dirty two tabs; Quit; Discard on the
   first prompt; Cancel on the second. Both tabs must still show unsaved text
   and dirty state. Quit again and Cancel immediately — buffers stay protected.
5. **Object Explorer chrome** — Expand Selected / Right-Enter-Space load
   children only; Insert SELECT is a separate command.
6. **Run guard / target** — without a server, Run asks to connect. After a live
   batch that leaves the target unknown (host required), Run refuses until
   reconnect/revalidate; SQL is never replayed.

Component suite covers deferred Open/Quit sequences, SqlDecimal typed path
(38-digit probe literal), mid-row budget + UI truncation marker, one-row-then-
throw reader, and SQL-vs-target verification helpers.

## Live checks (pending approved disposable host)

Wrong cert/password; connect; explorer; USE then error with target verify;
successful write then failed DB_NAME refresh; provider SqlDataReader
decimal(38) end-to-end; cancel/disconnect. Record arm64 and Intel separately
in STATE.md.

Credentials are session-only. Trust-certificate and pin path are lab options.
