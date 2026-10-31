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

## Host-independent checks (demonstrated without SQL Server)

1. **Connect dialog** — open Connect, omit required fields, confirm validation
   errors; Cancel leaves the workspace unchanged.
2. **Tabs** — New Query; type different SQL in two tabs; confirm Run mode flips
   between document and selection; Close Tab (Ctrl+W) prompts when dirty.
3. **Dirty lifecycle** — edit two tabs; Quit; Discard the first prompt; Cancel
   the second — second tab stays dirty and the app remains open. Repeat Quit and
   Discard both to exit. Native window close uses the same path (cancel first,
   then prompt).
4. **Open cancel** — dirty a tab; Open; Cancel at the unsaved prompt — text stays
   dirty.
5. **Object Explorer chrome** — without a connection, Refresh leaves the tree
   empty. With focus on the tree, Right/Enter/Space and **Object → Expand
   Selected** share the expand path. **Insert SELECT** is a separate command and
   does not run on expand.
6. **Run guard** — with no server, Run reports “Connect before running”. Escape
   Cancel with nothing running is a no-op message.

## Live checks (pending approved disposable host)

Exercise SPEC-002 live steps: wrong cert/password; connect; explorer across two
databases with identically named tables; two-tab run; USE target accuracy;
types/38-digit/late error; timeout vs disconnect; cancel verification; save
failure paths. Record evidence in STATE.md separately for arm64 and Intel.

Credentials are session-only. Trust-certificate and pin path are lab options and
are never silent defaults.
