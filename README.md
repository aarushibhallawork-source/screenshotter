# Screenshot Vault ♡

Stop screenshots from piling up on your Mac's Desktop.

Take a screenshot (⌘⇧3, ⌘⇧4 or ⌘⇧5) and it is:

1. **copied to your clipboard** straight away, ready to paste,
2. **renamed** to something readable like `2026-10-19 at 14.32.05 - Figma.png` (the time plus the app you were in),
3. **filed by date** in your vault, not on the Desktop.

You get a Y2K-style gallery to browse them and a desktop widget that takes its colours from your wallpaper.

## Install (no Terminal needed)

1. **[Download ScreenshotVault.dmg](https://github.com/aarushibhallawork-source/screenshotter/releases/latest/download/ScreenshotVault.dmg)**.
2. Open it, and drag **Screenshot Vault** onto the **Applications** folder.
3. Open **Screenshot Vault** from Applications, and click **Turn It On**.

### "Apple could not verify…" the first time you open it

Screenshot Vault isn't signed with a paid Apple developer certificate, so macOS asks you to confirm once.

**macOS 15 Sequoia or later:**
1. Open the app, and click **Done** on the warning.
2. Open **System Settings → Privacy & Security**, and scroll down.
3. Next to *"Screenshot Vault" was blocked*, click **Open Anyway**. Then click **Open Anyway** again and enter your password.

**macOS 13 Ventura or 14 Sonoma:** right-click the app in Applications, choose **Open**, then click **Open**.

After that it opens normally, and it starts by itself every time you log in.

Works on Apple silicon and Intel Macs running macOS 13 or later.

## Using it

**The ♡ icon in your menu bar** has everything:
- **Open Gallery**: browse your screenshots.
- **Show Desktop Widget**: turn the widget on or off.
- **Open at Login**
- **Move Desktop Screenshots into Vault**: tidy up the ones already on your Desktop.
- **Show Vault in Finder**
- **Turn Off & Restore Desktop Screenshots…**: undo everything.

Opening the app again from Applications also opens the gallery.

**Gallery**
- Screenshots are grouped by day. Filter them by app, or press **/** to search.
- Click **Copy**, or open a screenshot and press **C**. Use the arrow keys to move between them.
- **⌘-click** selects several screenshots so you can move them to the Trash together.
- **Auto-delete** can move screenshots older than 7, 30 or 90 days to the Trash. It's off by default.

**Desktop widget:** sits on your wallpaper, behind your apps and on every desktop. It shows your 6 latest screenshots.
- Click one to copy it. Right-click (two-finger click) one to copy, open, show in Finder or move it to the Trash.
- Drag the widget by its title bar. Right-click the title bar for Reload, Open Gallery or Hide Widget.
- Its colours come from your wallpaper and change within a few seconds of you changing wallpaper. If your wallpaper is in Downloads, Desktop or Documents, macOS asks once whether Screenshot Vault can read it. Click **Allow**.

## Good to know

- Screenshots live in `~/Library/Application Support/ScreenshotVault/`, out of sight but still on your Mac. Use Auto-delete if you'd like them cleared automatically.
- The floating thumbnail in the screen corner is turned off. It delays saving by about 5 seconds, so turning it off is what makes the clipboard copy instant.
- **⌘⌃⇧4** still works as normal: it copies to the clipboard only and saves nothing.
- Nothing leaves your Mac. The gallery runs on a private local address that only your Mac can reach.
- If you quit the app, new screenshots wait in the vault's inbox and are filed as soon as you open it again.

## Uninstall

Choose **♡ → Turn Off & Restore Desktop Screenshots…**, then drag Screenshot Vault from Applications to the Trash. Your vault folder is kept, so delete it as well if you don't want the screenshots any more.

---

## Building from source

You need Apple's Command Line Tools (`xcode-select --install`). Then:

```bash
./build.sh
```

That produces `build/Screenshot Vault.app` (Apple silicon + Intel) and `dist/ScreenshotVault.dmg`. To publish, create a GitHub release and attach the `.dmg`, keeping the name `ScreenshotVault.dmg` so the download link above keeps working.

| Path | What it is |
|---|---|
| `app/main.swift` | Menu-bar app: first-run setup, move to Applications, login item, gallery window, turning off |
| `app/Vault.swift` | Watches the screenshot inbox, copies to the clipboard, files by date, makes thumbnails, and moves files to the Trash |
| `app/Server.swift` | Tiny local web server (127.0.0.1:7333) for the gallery and widget pages |
| `app/Widget.swift` | Desktop widget window and wallpaper colour extraction |
| `web/gallery.html`, `web/widget.html` | The gallery and widget pages |
| `scripts/make-icon.swift` | Draws the app icon |
