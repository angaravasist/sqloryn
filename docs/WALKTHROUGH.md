# Reviewer walkthrough — Developer Preview 0

1. Install .NET SDK matching `global.json` (10.0.403 band).
2. From the repo root (paths with spaces are OK):

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore --configuration Release
bash scripts/build_macos_app.sh
open "artifacts/macos/$(uname -m | sed 's/arm64/osx-arm64/;s/x86_64/osx-x64/')/SQLoryn.app"
```

3. Without a server: open Connect, omit fields, confirm validation errors; create
   two tabs; type SQL; confirm Run mode switches when text is selected; Quit with
   a dirty tab and choose Cancel.
4. With an **approved** disposable SQL Server only: exercise SPEC-002 live steps
   (wrong cert/password, explorer, two-tab run, types/late error, cancel,
   disconnect uncertainty, save/discard). Record evidence in STATE.md.

Credentials are session-only. Trust-certificate and pin path are lab options and
are never silent defaults.
