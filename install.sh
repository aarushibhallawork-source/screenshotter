#!/bin/zsh
# Installs Screenshot Vault: background service + gallery app, and points macOS screenshots at its inbox.
set -euo pipefail

SRC="${0:A:h}"
ROOT="$HOME/Library/Application Support/ScreenshotVault"
BIN="$ROOT/bin"
LABEL="com.screenshotvault.agent"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
APP="$HOME/Applications/Screenshot Vault.app"

mkdir -p "$BIN" "$ROOT/inbox" "$ROOT/archive" "$ROOT/thumbs" "$HOME/Library/LaunchAgents" "$HOME/Applications"
# Run from ~/Library, not the Desktop: background agents can't read the (privacy-protected) Desktop.
cp "$SRC/vault.py" "$SRC/gallery.html" "$SRC/widget.html" "$BIN/"
chmod +x "$BIN/vault.py"

cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$LABEL</string>
  <key>ProgramArguments</key>
  <array><string>/usr/bin/python3</string><string>$BIN/vault.py</string><string>serve</string></array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ProcessType</key><string>Interactive</string>
  <key>StandardOutPath</key><string>$ROOT/vault.log</string>
  <key>StandardErrorPath</key><string>$ROOT/vault.log</string>
</dict>
</plist>
EOF

launchctl bootout "gui/$UID/$LABEL" 2>/dev/null || true
launchctl bootstrap "gui/$UID" "$PLIST"

# Point screenshots at the inbox and drop the floating thumbnail so files land instantly.
defaults write com.apple.screencapture location "$ROOT/inbox"
defaults write com.apple.screencapture show-thumbnail -bool false
killall SystemUIServer 2>/dev/null || true

# Tiny launcher app (Spotlight: "Screenshot Vault").
rm -rf "$APP"
osacompile -o "$APP" -e 'open location "http://localhost:7333"' >/dev/null

# Desktop widget: compile the native window, bundle it, and start it at login.
WAPP="$HOME/Applications/Screenshot Vault Widget.app"
WLABEL="com.screenshotvault.widget"
WPLIST="$HOME/Library/LaunchAgents/$WLABEL.plist"
launchctl bootout "gui/$UID/$WLABEL" 2>/dev/null || true
rm -rf "$WAPP"
mkdir -p "$WAPP/Contents/MacOS"
swiftc -O -o "$WAPP/Contents/MacOS/widget" "$SRC/widget/main.swift" -framework Cocoa -framework WebKit
cat > "$WAPP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>$WLABEL</string>
  <key>CFBundleName</key><string>Screenshot Vault Widget</string>
  <key>CFBundleExecutable</key><string>widget</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSUIElement</key><true/>
  <key>NSAppTransportSecurity</key><dict><key>NSAllowsLocalNetworking</key><true/></dict>
</dict>
</plist>
EOF
codesign --force --sign - "$WAPP" >/dev/null 2>&1

cat > "$WPLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$WLABEL</string>
  <key>ProgramArguments</key><array><string>$WAPP/Contents/MacOS/widget</string></array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
  <key>ProcessType</key><string>Interactive</string>
</dict>
</plist>
EOF
launchctl bootstrap "gui/$UID" "$WPLIST"

echo "Installed. Gallery: http://localhost:7333  (or open 'Screenshot Vault' from Spotlight)"
