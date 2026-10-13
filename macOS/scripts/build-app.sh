#!/bin/zsh
# Builds "AFAN Traders.app" into ./dist
set -e
cd "$(dirname "$0")/.."
APP="dist/AFAN Traders.app"

swift build -c release --arch arm64 --arch x86_64 2>/dev/null || swift build -c release
BIN=$(swift build -c release --show-bin-path 2>/dev/null)/AfanTraders
[ -f "$BIN" ] || BIN=".build/apple/Products/Release/AfanTraders"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/AfanTraders"
cp -R Resources/Icons "$APP/Contents/Resources/Icons"

# App icon
TMP=$(mktemp -d)
swift scripts/make-icon.swift "$TMP/icon.png"
mkdir "$TMP/AppIcon.iconset"
for s in 16 32 128 256 512; do
  sips -z $s $s "$TMP/icon.png" --out "$TMP/AppIcon.iconset/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$TMP/icon.png" --out "$TMP/AppIcon.iconset/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$TMP/AppIcon.iconset" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$TMP"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>AFAN Traders</string>
    <key>CFBundleDisplayName</key><string>AFAN Traders</string>
    <key>CFBundleIdentifier</key><string>com.afantraders.desktop</string>
    <key>CFBundleExecutable</key><string>AfanTraders</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0.0</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.business</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSHumanReadableCopyright</key><string>© AFAN Traders</string>
    <key>NSLocalNetworkUsageDescription</key><string>AFAN Traders finds the other office computers on your network to keep quotations, stock and invoices in sync.</string>
    <key>NSBonjourServices</key><array><string>_afansync._tcp</string></array>
</dict>
</plist>
PLIST

codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || true
echo "Built: $(pwd)/$APP"
