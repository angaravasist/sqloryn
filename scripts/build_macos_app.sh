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
rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS"
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet publish src/Sqloryn.App/Sqloryn.csproj -c Release --self-contained false \
  --no-restore -o "$APP/Contents/MacOS"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleExecutable</key><string>Sqloryn</string>
  <key>CFBundleIdentifier</key><string>org.sqloryn.app</string>
  <key>CFBundleName</key><string>SQLoryn</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.0.0</string>
  <key>CFBundleVersion</key><string>0</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
echo "Built $APP ($ARCH, framework-dependent)"
echo "Launch: open \"$APP\""
