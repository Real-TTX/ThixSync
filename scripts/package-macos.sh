#!/bin/sh
# Packs a published macOS build into ThixSync.app, a zip (for the update of installed versions) and a disk image
# (for the first installation: drag ThixSync into "Programme").
#
#   scripts/package-macos.sh <publish folder> <rid: osx-arm64|osx-x64> <version> <build number> <output folder>
#
# Runs on macOS (iconutil, codesign, ditto, hdiutil). The app has a window and a Dock icon and keeps a menu bar icon.
# Without an Apple Developer ID the app is signed ad hoc (needed on Apple Silicon); Gatekeeper then asks once
# (right click > Öffnen, or System Settings > Privacy & Security). Analysing protected folders (Desktop, Documents,
# Downloads, other volumes) asks for access once; "Festplattenvollzugriff" lets ThixSync read everything.
set -eu

publish="$1"
rid="$2"
version="$3"
build="$4"
out="$5"

root="$(cd "$(dirname "$0")/.." && pwd)"
arch="${rid#osx-}"
major=$(sed -n 's:.*<ThixSyncMajor>\(.*\)</ThixSyncMajor>.*:\1:p' "$root/Directory.Build.props")
minor=$(sed -n 's:.*<ThixSyncMinor>\(.*\)</ThixSyncMinor>.*:\1:p' "$root/Directory.Build.props")
work="$(mktemp -d)"
app="$work/ThixSync.app"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$out"
cp -R "$publish/." "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/ThixSync"

# App icon from the rendered branding (tools/render-icons.js): the macOS variant keeps Apple's margin around the tile.
iconset="$work/ThixSync.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  cp "$root/branding/png/thixsync-mac-${size}.png" "$iconset/icon_${size}x${size}.png"
  double=$((size * 2))
  cp "$root/branding/png/thixsync-mac-${double}.png" "$iconset/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/ThixSync.icns"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key>
  <string>info.schmoldt.thixsync</string>
  <key>CFBundleName</key>
  <string>ThixSync</string>
  <key>CFBundleDisplayName</key>
  <string>ThixSync</string>
  <key>CFBundleExecutable</key>
  <string>ThixSync</string>
  <key>CFBundleIconFile</key>
  <string>ThixSync</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${major}.${minor}.${build}</string>
  <key>CFBundleVersion</key>
  <string>${build}</string>
  <key>CFBundleGetInfoString</key>
  <string>ThixSync ${version}</string>
  <key>NSHumanReadableCopyright</key>
  <string>© Matthias Schmoldt</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>CFBundleDevelopmentRegion</key>
  <string>de</string>
  <key>NSDesktopFolderUsageDescription</key>
  <string>ThixSync zeigt, wie viel Platz die Dateien auf deinem Schreibtisch belegen.</string>
  <key>NSDocumentsFolderUsageDescription</key>
  <string>ThixSync zeigt, wie viel Platz deine Dokumente belegen.</string>
  <key>NSDownloadsFolderUsageDescription</key>
  <string>ThixSync zeigt, wie viel Platz deine Downloads belegen.</string>
  <key>NSRemovableVolumesUsageDescription</key>
  <string>ThixSync analysiert auf Wunsch auch externe Laufwerke.</string>
  <key>NSNetworkVolumesUsageDescription</key>
  <string>ThixSync analysiert auf Wunsch Netzwerkfreigaben (SMB).</string>
</dict>
</plist>
PLIST

# Ad-hoc signature: Apple Silicon runs only signed code. A Developer ID signature + notarization can replace it later.
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

# Update package (MacAppBundle unpacks it with ditto) and disk image for the first installation.
ditto -c -k --keepParent "$app" "$out/ThixSync-macOS-${arch}.zip"

dmg_root="$work/dmg"
mkdir -p "$dmg_root"
cp -R "$app" "$dmg_root/"
ln -s /Applications "$dmg_root/Programme"
hdiutil create -volname "ThixSync" -srcfolder "$dmg_root" -ov -format UDZO "$out/ThixSync-macOS-${arch}.dmg" >/dev/null

rm -rf "$work"
ls -l "$out"
