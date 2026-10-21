// Screenshot Vault: app entry point. A menu-bar app that runs the vault, the local
// server, the desktop widget and the gallery window, and handles setup and teardown.
import Cocoa
import WebKit

let bundleID = Bundle.main.bundleIdentifier ?? "com.screenshotvault.app"
let loginLabel = "com.screenshotvault.app"
let loginPlist = Paths.home.appendingPathComponent("Library/LaunchAgents/\(loginLabel).plist")

@discardableResult
func run(_ tool: String, _ args: [String]) -> Int32 {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: tool)
    p.arguments = args
    p.standardOutput = FileHandle.nullDevice
    p.standardError = FileHandle.nullDevice
    do { try p.run(); p.waitUntilExit(); return p.terminationStatus } catch { return -1 }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, WKUIDelegate, NSMenuDelegate {
    let server = Server()
    let widget = WidgetController()
    var statusItem: NSStatusItem!
    var gallery: NSWindow?
    let defaults = UserDefaults.standard

    var isSetUp: Bool {
        get { defaults.bool(forKey: "setupDone") }
        set { defaults.set(newValue, forKey: "setupDone") }
    }
    var widgetOn: Bool {
        get { defaults.object(forKey: "widgetOn") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "widgetOn") }
    }

    // MARK: launch

    func applicationDidFinishLaunching(_ note: Notification) {
        // Only one copy at a time: hand over to the running one.
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: bundleID)
            .filter { $0 != NSRunningApplication.current }
        if let other = others.first {
            other.activate()
            NSWorkspace.shared.openApplication(at: other.bundleURL ?? Bundle.main.bundleURL,
                                               configuration: NSWorkspace.OpenConfiguration())
            exit(0)
        }

        buildMainMenu()
        if offerMoveToApplications() { return }
        migrateOldInstall()

        Vault.shared.prepare()
        server.onOpenGallery = { [weak self] in self?.showGallery() }
        server.onFatal = { why in
            self.alert("Screenshot Vault couldn't start",
                       "Something else on this Mac is using port \(port). Restart your Mac and open Screenshot Vault again.\n\n(\(why))")
        }
        server.start()
        Vault.shared.startWatching()

        widget.onOpenGallery = { [weak self] in self?.showGallery() }
        widget.onHide = { [weak self] in self?.widgetOn = false }
        buildStatusItem()

        if isSetUp {
            if widgetOn { widget.show() }
        } else {
            DispatchQueue.main.async { self.welcome() }
        }
    }

    // Double-clicking the app again opens the gallery.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if isSetUp { showGallery() } else { welcome() }
        return true
    }

    // MARK: first run

    func welcome() {
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = "Welcome to Screenshot Vault ♡"
        a.informativeText = """
        From now on, every screenshot you take (⌘⇧3, ⌘⇧4, ⌘⇧5) is copied to your clipboard right away \
        and filed by date in your vault, not on your Desktop.

        Find them any time in the gallery (the ♡ icon in your menu bar) or in the little widget on your desktop.
        """
        a.addButton(withTitle: "Turn It On")
        a.addButton(withTitle: "Quit")
        guard a.runModal() == .alertFirstButtonReturn else { NSApp.terminate(nil); return }

        turnOn()
        let loose = Vault.shared.desktopScreenshots()
        if !loose.isEmpty {
            let b = NSAlert()
            b.messageText = "Tidy up your Desktop too?"
            b.informativeText = "There \(loose.count == 1 ? "is 1 screenshot" : "are \(loose.count) screenshots") on your Desktop. Move \(loose.count == 1 ? "it" : "them") into the vault?"
            b.addButton(withTitle: "Move Them")
            b.addButton(withTitle: "Not Now")
            if b.runModal() == .alertFirstButtonReturn { _ = Vault.shared.importFiles(loose) }
        }
        widgetOn = true
        widget.show()
        showGallery()
    }

    func turnOn() {
        Vault.shared.prepare()
        run("/usr/bin/defaults", ["write", "com.apple.screencapture", "location", Paths.inbox.path])
        // The floating thumbnail delays saving by ~5s; without it the clipboard copy is instant.
        run("/usr/bin/defaults", ["write", "com.apple.screencapture", "show-thumbnail", "-bool", "false"])
        run("/usr/bin/killall", ["SystemUIServer"])
        setOpenAtLogin(true)
        isSetUp = true
    }

    // MARK: Applications folder

    /// Offers to copy the app into Applications when it's run from a disk image or Downloads.
    /// Returns true if it relaunched from the new location.
    func offerMoveToApplications() -> Bool {
        let path = Bundle.main.bundlePath
        guard path.hasPrefix("/Volumes/") || path.contains("/AppTranslocation/") || path.contains("/Downloads/") else { return false }
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = "Move to Applications?"
        a.informativeText = "Screenshot Vault needs to live in your Applications folder so it can start when you log in."
        a.addButton(withTitle: "Move to Applications")
        a.addButton(withTitle: "Not Now")
        guard a.runModal() == .alertFirstButtonReturn else { return false }

        let fm = FileManager.default
        for dir in [URL(fileURLWithPath: "/Applications"), Paths.home.appendingPathComponent("Applications")] {
            let dest = dir.appendingPathComponent("Screenshot Vault.app")
            try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
            if fm.fileExists(atPath: dest.path) { try? fm.trashItem(at: dest, resultingItemURL: nil) }
            do {
                try fm.copyItem(at: Bundle.main.bundleURL, to: dest)
            } catch { continue }
            // You've already chosen to open it; don't make macOS ask again for the copy.
            run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", dest.path])
            let cfg = NSWorkspace.OpenConfiguration()
            cfg.createsNewApplicationInstance = true
            NSWorkspace.shared.openApplication(at: dest, configuration: cfg) { _, _ in
                DispatchQueue.main.async { exit(0) }
            }
            return true
        }
        alert("Couldn't move it", "Drag Screenshot Vault into your Applications folder yourself, then open it from there.")
        return false
    }

    // MARK: login item

    var opensAtLogin: Bool { FileManager.default.fileExists(atPath: loginPlist.path) }

    func setOpenAtLogin(_ on: Bool) {
        guard on else { try? FileManager.default.removeItem(at: loginPlist); return }
        let plist: [String: Any] = [
            "Label": loginLabel,
            "ProgramArguments": [Bundle.main.executablePath ?? ""],
            "RunAtLoad": true,
            // Restart after a crash, but stay closed if you choose Quit.
            "KeepAlive": ["SuccessfulExit": false],
            "ProcessType": "Interactive",
        ]
        try? FileManager.default.createDirectory(at: loginPlist.deletingLastPathComponent(), withIntermediateDirectories: true)
        (plist as NSDictionary).write(to: loginPlist, atomically: true)
    }

    /// Earlier versions ran as a Python service plus a separate widget app; retire them.
    func migrateOldInstall() {
        let fm = FileManager.default
        var found = false
        for label in ["com.screenshotvault.agent", "com.screenshotvault.widget"] {
            let p = Paths.home.appendingPathComponent("Library/LaunchAgents/\(label).plist")
            guard fm.fileExists(atPath: p.path) else { continue }
            run("/bin/launchctl", ["bootout", "gui/\(getuid())/\(label)"])
            try? fm.removeItem(at: p)
            found = true
        }
        let apps = Paths.home.appendingPathComponent("Applications")
        try? fm.removeItem(at: apps.appendingPathComponent("Screenshot Vault Widget.app"))
        let launcher = apps.appendingPathComponent("Screenshot Vault.app")
        if launcher.standardizedFileURL != Bundle.main.bundleURL.standardizedFileURL,
           fm.fileExists(atPath: launcher.appendingPathComponent("Contents/Resources/Scripts/main.scpt").path) {
            try? fm.removeItem(at: launcher)
        }
        try? fm.removeItem(at: Paths.root.appendingPathComponent("bin"))
        if found {
            isSetUp = true
            setOpenAtLogin(true)
        }
    }

    // MARK: turning off

    @objc func turnOff() {
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = "Turn off Screenshot Vault?"
        a.informativeText = """
        Screenshots will go back to saving on your Desktop, and the app won't open at login any more.

        Your vault stays where it is (~/Library/Application Support/ScreenshotVault). \
        After this you can drag Screenshot Vault from Applications to the Trash.
        """
        a.addButton(withTitle: "Turn Off")
        a.addButton(withTitle: "Cancel")
        guard a.runModal() == .alertFirstButtonReturn else { return }
        run("/usr/bin/defaults", ["write", "com.apple.screencapture", "location", Paths.home.appendingPathComponent("Desktop").path])
        run("/usr/bin/defaults", ["delete", "com.apple.screencapture", "show-thumbnail"])
        run("/usr/bin/killall", ["SystemUIServer"])
        setOpenAtLogin(false)
        isSetUp = false
        NSApp.terminate(nil)
    }

    // MARK: gallery window

    @objc func showGallery() {
        if gallery == nil {
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1180, height: 800),
                             styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
            w.title = "Screenshot Vault"
            w.isReleasedWhenClosed = false
            w.minSize = NSSize(width: 420, height: 420)
            w.setFrameAutosaveName("ScreenshotVaultGallery")
            w.delegate = self
            let web = WKWebView(frame: w.contentView!.bounds)
            web.autoresizingMask = [.width, .height]
            web.uiDelegate = self
            web.load(URLRequest(url: baseURL, cachePolicy: .reloadIgnoringLocalCacheData))
            w.contentView = web
            w.center()
            gallery = w
        }
        // Show in the Dock and ⌘-Tab while the gallery is open.
        NSApp.setActivationPolicy(.regular)
        gallery?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func windowWillClose(_ note: Notification) {
        if (note.object as? NSWindow) == gallery { NSApp.setActivationPolicy(.accessory) }
    }

    // The gallery uses confirm() before trashing several items.
    func webView(_ w: WKWebView, runJavaScriptConfirmPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (Bool) -> Void) {
        let a = NSAlert()
        a.messageText = message
        a.addButton(withTitle: "OK")
        a.addButton(withTitle: "Cancel")
        completionHandler(a.runModal() == .alertFirstButtonReturn)
    }

    func webView(_ w: WKWebView, runJavaScriptAlertPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping () -> Void) {
        alert(message, "")
        completionHandler()
    }

    // MARK: menus

    func buildStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let img = NSImage(systemSymbolName: "heart.text.square", accessibilityDescription: "Screenshot Vault") {
            img.isTemplate = true
            statusItem.button?.image = img
        } else {
            statusItem.button?.title = "♡"
        }
        let m = NSMenu()
        m.delegate = self
        statusItem.menu = m
    }

    // Rebuilt each time it opens so the checkmarks are current.
    func menuNeedsUpdate(_ m: NSMenu) {
        m.removeAllItems()
        if !isSetUp {
            m.addItem(item("Turn On Screenshot Vault…", #selector(welcomeFromMenu)))
            m.addItem(.separator())
            m.addItem(item("Quit Screenshot Vault", #selector(quit)))
            return
        }
        m.addItem(item("Open Gallery", #selector(showGallery)))
        let w = item("Show Desktop Widget", #selector(toggleWidget))
        w.state = widget.isVisible ? .on : .off
        m.addItem(w)
        let l = item("Open at Login", #selector(toggleLogin))
        l.state = opensAtLogin ? .on : .off
        m.addItem(l)
        m.addItem(.separator())
        m.addItem(item("Move Desktop Screenshots into Vault", #selector(importDesktop)))
        m.addItem(item("Show Vault in Finder", #selector(revealVault)))
        m.addItem(.separator())
        m.addItem(item("Turn Off & Restore Desktop Screenshots…", #selector(turnOff)))
        m.addItem(item("Quit Screenshot Vault", #selector(quit)))
    }

    func item(_ title: String, _ sel: Selector) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: sel, keyEquivalent: "")
        i.target = self
        return i
    }

    @objc func welcomeFromMenu() { welcome() }

    @objc func toggleWidget() {
        if widget.isVisible { widget.hide(); widgetOn = false } else { widget.show(); widgetOn = true }
    }

    @objc func toggleLogin() { setOpenAtLogin(!opensAtLogin) }

    @objc func importDesktop() {
        let n = Vault.shared.importFiles(Vault.shared.desktopScreenshots())
        NSApp.activate(ignoringOtherApps: true)
        alert(n == 0 ? "No screenshots on your Desktop" : "Moved \(n) screenshot\(n == 1 ? "" : "s") into the vault ♡", "")
    }

    @objc func revealVault() { NSWorkspace.shared.activateFileViewerSelecting([Paths.archive]) }

    @objc func quit() { NSApp.terminate(nil) }

    func alert(_ title: String, _ text: String) {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        a.runModal()
    }

    /// Standard app + Edit menus, so copy/paste and ⌘W/⌘Q work in the gallery window.
    func buildMainMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "About Screenshot Vault", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Hide Screenshot Vault", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "Quit Screenshot Vault", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        main.addItem(editItem)

        let winItem = NSMenuItem()
        let win = NSMenu(title: "Window")
        win.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        win.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        winItem.submenu = win
        main.addItem(winItem)
        NSApp.mainMenu = main
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
