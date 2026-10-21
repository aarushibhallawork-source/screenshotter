# Screenshot Vault ♡

Stop screenshots from piling up on your Mac's Desktop.

Take a screenshot (⌘⇧3, ⌘⇧4 or ⌘⇧5) and it is:

1. **copied to your clipboard** straight away, ready to paste,
2. **renamed** to something readable like `2026-10-19 at 14.32.05 - Figma.png` (the time plus the app you were in),
3. **filed by date** under `~/Library/Application Support/ScreenshotVault/archive/YYYY-MM/YYYY-MM-DD/`,
4. **removed** from where macOS dropped it, so nothing lands on the Desktop.

You get a Y2K-style gallery to browse them and a desktop widget that takes its colours from your wallpaper.

## Install on a Mac

Requirements: macOS 13 or later, and Apple's Command Line Tools (for Python and Swift). If you don't have them yet, install them first:

```bash
xcode-select --install
```

Then:

```bash
git clone https://github.com/aarushibhallawork-source/screenshotter.git
cd screenshotter
./install.sh
```

That's it. Take a screenshot to try it.

> This works on Macs only. It relies on macOS's screenshot settings, `launchd` and AppKit.

## Using it

**Gallery:** open **Screenshot Vault** from Spotlight, or go to http://localhost:7333.
- Screenshots are grouped by day. Filter them by app, or press **/** to search.
- Click **Copy** on any screenshot, or open one and press **C**. Use the arrow keys to move between them.
- **⌘-click** selects several screenshots so you can move them to the Trash together.
- **Auto-delete** can move screenshots older than 7, 30 or 90 days to the Trash. It's off by default.

**Desktop widget:** a small window pinned to your wallpaper, behind your apps and on every desktop. It shows your 6 latest screenshots.
- Click a screenshot to copy it.
- Right-click (two-finger click) one to copy, open, show in Finder or move it to the Trash.
- Drag the widget by its title bar. Right-click the title bar for Reload, Open Vault or Quit Widget.
- It takes its colours from your current wallpaper and updates within a few seconds when you change it. If your wallpaper is in Downloads, Desktop or Documents, macOS asks once for access. Click Allow.
- It starts at login. To bring it back after quitting it, run:

```bash
launchctl kickstart gui/$UID/com.screenshotvault.widget
```

**Move old screenshots into the vault:**

```bash
/usr/bin/python3 ~/Library/Application\ Support/ScreenshotVault/bin/vault.py import ~/Desktop/Screenshot*.png
```

## Good to know

- Screenshots still use disk space; they're just kept out of sight in `~/Library` instead of on your Desktop.
- The floating thumbnail in the screen corner is turned off. It delays saving by about 5 seconds, so turning it off is what makes the clipboard copy instant.
- **⌘⌃⇧4** still works as normal: it copies to the clipboard only and saves nothing.
- Everything stays on your Mac. The gallery server only listens on `localhost`.
- Logs are in `~/Library/Application Support/ScreenshotVault/vault.log`.

## Update or uninstall

To update, run `git pull`, then `./install.sh` again.

To uninstall, run `./uninstall.sh`. Screenshots go back to saving on the Desktop, and your archive is kept.

## What's inside

| File | What it does |
|---|---|
| `vault.py` | Watches the screenshot inbox, copies to the clipboard, files by date, and serves the gallery (Python standard library only) |
| `gallery.html` | The full gallery page |
| `widget.html` | The desktop widget's page |
| `widget/main.swift` | The native frameless desktop window, plus the wallpaper colour extraction |
| `install.sh` / `uninstall.sh` | Set up or remove the background services, the launcher app and the screenshot settings |
