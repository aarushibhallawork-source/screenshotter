#!/bin/zsh
# Builds "Screenshot Vault.app" (Apple silicon + Intel) and dist/ScreenshotVault.dmg.
# Needs Apple's Command Line Tools (xcode-select --install). Only for people building from source;
# everyone else just downloads the .dmg from the Releases page.
set -euo pipefail
cd "${0:A:h}"

APP="build/Screenshot Vault.app"
rm -rf build
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" dist

echo "→ compiling"
for arch in arm64 x86_64; do
  swiftc -O -target "$arch-apple-macos13.0" -o "build/ScreenshotVault-$arch" app/*.swift \
    -framework Cocoa -framework WebKit -framework Network
done
lipo -create -output "$APP/Contents/MacOS/ScreenshotVault" build/ScreenshotVault-arm64 build/ScreenshotVault-x86_64

echo "→ resources"
cp app/Info.plist "$APP/Contents/Info.plist"
cp web/gallery.html web/widget.html "$APP/Contents/Resources/"

echo "→ icon"
swift scripts/make-icon.swift build/icon-1024.png
ICONSET=build/AppIcon.iconset
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s build/icon-1024.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s * 2)) $((s * 2)) build/icon-1024.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

echo "→ signing (ad-hoc)"
codesign --force --deep --sign - "$APP"

echo "→ disk image"
STAGE=build/dmg
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -quiet -volname "Screenshot Vault" -srcfolder "$STAGE" -ov -format UDZO dist/ScreenshotVault.dmg

echo "✓ built: $APP"
echo "✓ disk image: dist/ScreenshotVault.dmg"
