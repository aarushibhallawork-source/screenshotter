#!/bin/zsh
# Stops Screenshot Vault and restores normal screenshot behaviour. Your archive is left in place.
LABEL="com.screenshotvault.agent"
launchctl bootout "gui/$UID/$LABEL" 2>/dev/null
rm -f "$HOME/Library/LaunchAgents/$LABEL.plist"
rm -rf "$HOME/Applications/Screenshot Vault.app"
launchctl bootout "gui/$UID/com.screenshotvault.widget" 2>/dev/null
rm -f "$HOME/Library/LaunchAgents/com.screenshotvault.widget.plist"
rm -rf "$HOME/Applications/Screenshot Vault Widget.app"
defaults write com.apple.screencapture location "$HOME/Desktop"
defaults delete com.apple.screencapture show-thumbnail 2>/dev/null
killall SystemUIServer 2>/dev/null
echo "Uninstalled. Archive kept at: $HOME/Library/Application Support/ScreenshotVault/archive"
