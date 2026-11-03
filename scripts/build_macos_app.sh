#!/usr/bin/env bash
# Build a framework-dependent SQLoryn.app for the current Mac (.NET 10 required).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
case "$(uname -m)" in
  arm64) ARCH=osx-arm64 ;;
  x86_64) ARCH=osx-x64 ;;
  *) echo "Unsupported Mac architecture: $(uname -m)" >&2; exit 1 ;;
esac
OUT="$ROOT/artifacts/macos/$ARCH"
APP="$OUT/SQLoryn.app"
ICON_SRC="$ROOT/assets/icon/SQLoryn.icns"
SELF_CONTAINED="${SQLORYN_SELF_CONTAINED:-0}"
rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
if [[ ! -f "$ICON_SRC" ]]; then
  echo "Missing required icon: $ICON_SRC" >&2
  exit 1
fi
# Prefer repo-local or PATH dotnet that matches global.json
DOTNET_BIN="${DOTNET_ROOT:+$DOTNET_ROOT/dotnet}"
if [[ -z "${DOTNET_BIN}" || ! -x "${DOTNET_BIN}" ]]; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    DOTNET_BIN="$HOME/.dotnet/dotnet"
  else
    DOTNET_BIN="dotnet"
  fi
fi
"$DOTNET_BIN" restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
PUBLISH_ARGS=(publish src/Sqloryn.App/Sqloryn.csproj -c Release --no-restore -o "$APP/Contents/MacOS" -r "$ARCH")
if [[ "$SELF_CONTAINED" == "1" ]]; then
  PUBLISH_ARGS+=(--self-contained true)
else
  PUBLISH_ARGS+=(--self-contained false)
fi
"$DOTNET_BIN" "${PUBLISH_ARGS[@]}"
cp "$ICON_SRC" "$APP/Contents/Resources/SQLoryn.icns"
# Minimal Info.plist lint: required keys must be present after write
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleExecutable</key><string>Sqloryn</string>
  <key>CFBundleIdentifier</key><string>org.sqloryn.app</string>
  <key>CFBundleName</key><string>SQLoryn</string>
  <key>CFBundleDisplayName</key><string>SQLoryn</string>
  <key>CFBundleIconFile</key><string>SQLoryn</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Copyright SQLoryn contributors. Independent open-source project; not affiliated with Microsoft.</string>
</dict></plist>
PLIST
for key in CFBundleExecutable CFBundleIdentifier CFBundleName CFBundleIconFile; do
  /usr/libexec/PlistBuddy -c "Print :$key" "$APP/Contents/Info.plist" >/dev/null \
    || { echo "Info.plist missing $key" >&2; exit 1; }
done
MODE="framework-dependent"
[[ "$SELF_CONTAINED" == "1" ]] && MODE="self-contained"
echo "Built $APP ($ARCH, $MODE)"
echo "Launch: open \"$APP\""
echo "Note: unsigned internal candidate only — not Gatekeeper-notarised public distribution."
