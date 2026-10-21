// Desktop widget: a frameless window pinned to the desktop (above the wallpaper,
// below your apps, on every Space) showing localhost:7333/widget, themed from the wallpaper.
import Cocoa
import WebKit

let widgetURL = URL(string: "http://localhost:7333/widget")!
let widgetSize = NSSize(width: 330, height: 380)

// MARK: - Wallpaper palette

struct HSL { var h, s, l: Double }

func toHSL(_ r: Double, _ g: Double, _ b: Double) -> HSL {
    let mx = max(r, g, b), mn = min(r, g, b), l = (mx + mn) / 2
    guard mx != mn else { return HSL(h: 0, s: 0, l: l) }
    let d = mx - mn, s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn)
    var h: Double
    if mx == r { h = (g - b) / d + (g < b ? 6 : 0) } else if mx == g { h = (b - r) / d + 2 } else { h = (r - g) / d + 4 }
    return HSL(h: h * 60, s: s, l: l)
}

func css(_ h: Double, _ s: Double, _ l: Double) -> String {
    String(format: "hsl(%.0f %.0f%% %.0f%%)", h, min(max(s, 0), 1) * 100, min(max(l, 0), 1) * 100)
}

/// Samples the wallpaper at low resolution, clusters its colours, and maps the
/// darkest / lightest / most saturated clusters onto the widget's CSS variables.
func wallpaperTheme(_ wallpaper: URL) -> [String: String]? {
    var url = wallpaper
    // Apple's built-in (aerial/dynamic) wallpapers are .madesktop plists pointing at a still thumbnail.
    if url.pathExtension == "madesktop",
       let dict = NSDictionary(contentsOf: url), let thumb = dict["thumbnailPath"] as? String {
        url = URL(fileURLWithPath: thumb)
    }
    guard let src = CGImageSourceCreateWithURL(url as CFURL, nil),
          let img = CGImageSourceCreateThumbnailAtIndex(src, 0, [
              kCGImageSourceCreateThumbnailFromImageAlways: true,
              kCGImageSourceThumbnailMaxPixelSize: 128] as CFDictionary) else { return nil }
    let w = 48, h = 48
    var px = [UInt8](repeating: 0, count: w * h * 4)
    guard let ctx = CGContext(data: &px, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                              space: CGColorSpace(name: CGColorSpace.sRGB)!,
                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
    ctx.draw(img, in: CGRect(x: 0, y: 0, width: w, height: h))

    // Bucket into 8 levels per channel, then merge buckets that look alike.
    var sums = [Int: (Double, Double, Double, Int)]()
    for i in stride(from: 0, to: px.count, by: 4) {
        let r = Double(px[i]) / 255, g = Double(px[i + 1]) / 255, b = Double(px[i + 2]) / 255
        let key = (Int(r * 7.99) << 6) | (Int(g * 7.99) << 3) | Int(b * 7.99)
        let s = sums[key] ?? (0, 0, 0, 0)
        sums[key] = (s.0 + r, s.1 + g, s.2 + b, s.3 + 1)
    }
    var clusters: [(r: Double, g: Double, b: Double, n: Int)] = []
    for s in sums.values.sorted(by: { $0.3 > $1.3 }) {
        let c = (r: s.0 / Double(s.3), g: s.1 / Double(s.3), b: s.2 / Double(s.3))
        if let i = clusters.firstIndex(where: { pow($0.r - c.r, 2) + pow($0.g - c.g, 2) + pow($0.b - c.b, 2) < 0.03 }) {
            let k = clusters[i], n = k.n + s.3
            clusters[i] = ((k.r * Double(k.n) + s.0) / Double(n), (k.g * Double(k.n) + s.1) / Double(n),
                           (k.b * Double(k.n) + s.2) / Double(n), n)
        } else {
            clusters.append((c.r, c.g, c.b, s.3))
        }
    }
    let total = Double(w * h)
    var sig = clusters.filter { Double($0.n) / total >= 0.04 }
    if sig.isEmpty { sig = Array(clusters.prefix(3)) }
    let cols = sig.map { toHSL($0.r, $0.g, $0.b) }
    guard let dark = cols.min(by: { $0.l < $1.l }), let light = cols.max(by: { $0.l < $1.l }) else { return nil }
    let accent = cols.filter { $0.s > 0.25 && $0.l > 0.15 && $0.l < 0.85 }.max(by: { $0.s < $1.s }) ?? dark

    let ls = min(light.s, 0.65)
    return [
        "ink": css(dark.h, min(dark.s, 0.7), min(max(dark.l, 0.10), 0.22)),
        "pink": css(light.h, ls, min(max(light.l, 0.78), 0.86)),
        "pink-soft": css(light.h, ls, 0.89),
        "pink-wash": css(light.h, ls, 0.94),
        "paper": css(light.h, min(ls, 0.5), 0.975),
        "cream": css(light.h, min(ls, 0.5), 0.975),
        "rose": css(accent.h, min(max(accent.s, 0.5), 0.75), 0.45),
        "muted": css(dark.h, dark.s * 0.45, 0.45),
    ]
}

final class WidgetWindow: NSWindow {
    override var canBecomeKey: Bool { true }
}

final class ClickThroughWebView: WKWebView {
    // Respond to the first click even though the widget never becomes the active app.
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func willOpenMenu(_ menu: NSMenu, with event: NSEvent) { menu.removeAllItems() }
}

/// Invisible strip over the title bar that lets you drag the widget around.
final class DragHandle: NSView {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) { window?.performDrag(with: event) }
    override func rightMouseDown(with event: NSEvent) {
        if let menu = menu { NSMenu.popUpContextMenu(menu, with: event, for: self) }
    }
}

/// Bottom-right corner of the widget: drag to resize it, keeping its top-left corner in place.
final class GripView: NSView {
    private var startMouse = NSPoint.zero
    private var startFrame = NSRect.zero

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func resetCursorRects() {
        if #available(macOS 15.0, *) {
            addCursorRect(bounds, cursor: .frameResize(position: .bottomRight, directions: .all))
        } else {
            addCursorRect(bounds, cursor: .crosshair)
        }
    }
    override func mouseDown(with event: NSEvent) {
        startMouse = NSEvent.mouseLocation
        startFrame = window?.frame ?? .zero
    }
    override func mouseDragged(with event: NSEvent) {
        guard let w = window else { return }
        let p = NSEvent.mouseLocation
        let width = min(max(startFrame.width + p.x - startMouse.x, w.minSize.width), w.maxSize.width)
        let height = min(max(startFrame.height - (p.y - startMouse.y), w.minSize.height), w.maxSize.height)
        w.setFrame(NSRect(x: startFrame.minX, y: startFrame.maxY - height, width: width, height: height), display: true)
    }
}

final class WidgetController: NSObject, WKNavigationDelegate {
    var window: WidgetWindow!
    var web: ClickThroughWebView!
    var theme: [String: String]?
    var wallpaperKey = ""
    var onOpenGallery: () -> Void = {}
    var onHide: () -> Void = {}
    private var started = false

    var isVisible: Bool { window?.isVisible ?? false }

    func show() {
        if !started { build(); started = true }
        window.orderFront(nil)
    }

    func hide() { window?.orderOut(nil) }

    private func build() {
        let screen = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let origin = NSPoint(x: screen.maxX - widgetSize.width - 24, y: screen.maxY - widgetSize.height - 24)
        window = WidgetWindow(contentRect: NSRect(origin: origin, size: widgetSize),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = false
        window.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) + 1)
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenNone]
        window.minSize = NSSize(width: 260, height: 300)
        window.maxSize = NSSize(width: 1400, height: 1200)
        window.setFrameAutosaveName("ScreenshotVaultWidget") // remembers position and size

        web = ClickThroughWebView(frame: NSRect(origin: .zero, size: widgetSize))
        web.autoresizingMask = [.width, .height]
        web.setValue(false, forKey: "drawsBackground")
        web.navigationDelegate = self

        let handle = DragHandle(frame: NSRect(x: 4, y: widgetSize.height - 32, width: widgetSize.width - 80, height: 28))
        handle.autoresizingMask = [.minYMargin, .width]
        let menu = NSMenu()
        menu.addItem(withTitle: "Reload", action: #selector(reload), keyEquivalent: "").target = self
        menu.addItem(withTitle: "Open Gallery", action: #selector(openVault), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "Hide Widget", action: #selector(hideFromMenu), keyEquivalent: "").target = self
        handle.menu = menu

        let root = NSView(frame: NSRect(origin: .zero, size: widgetSize))
        // Sits over the grip drawn in the page's bottom-right corner.
        let grip = GripView(frame: NSRect(x: widgetSize.width - 32, y: 6, width: 26, height: 26))
        grip.autoresizingMask = [.minXMargin, .maxYMargin]
        root.addSubview(web)
        root.addSubview(handle)
        root.addSubview(grip)
        window.contentView = root

        load()
        refreshTheme()
        // macOS has no "wallpaper changed" event, so check cheaply every few seconds,
        // plus whenever the Space or display setup changes.
        Timer.scheduledTimer(withTimeInterval: 4, repeats: true) { _ in self.refreshTheme() }
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.activeSpaceDidChangeNotification,
                                                          object: nil, queue: .main) { _ in self.refreshTheme() }
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                               object: nil, queue: .main) { _ in self.refreshTheme() }
    }

    private var themeInFlight = false

    func refreshTheme() {
        guard !themeInFlight, let screen = window.screen ?? NSScreen.main,
              let url = NSWorkspace.shared.desktopImageURL(for: screen) else { return }
        themeInFlight = true
        let lastKey = wallpaperKey
        // All file access off the main thread: if the wallpaper is in a protected folder,
        // macOS blocks the reading thread until the access prompt is answered.
        DispatchQueue.global(qos: .utility).async {
            let mtime = (try? FileManager.default.attributesOfItem(atPath: url.path)[.modificationDate] as? Date)?
                .timeIntervalSince1970 ?? 0
            let key = "\(url.path)|\(mtime)"
            let t = key == lastKey ? nil : wallpaperTheme(url)
            DispatchQueue.main.async {
                self.themeInFlight = false
                guard key != lastKey else { return }
                self.wallpaperKey = key
                guard let t = t else { return }   // unreadable wallpaper: keep current colours
                self.theme = t
                self.applyTheme()
            }
        }
    }

    func applyTheme() {
        guard let t = theme, let data = try? JSONSerialization.data(withJSONObject: t),
              let json = String(data: data, encoding: .utf8) else { return }
        web.evaluateJavaScript("window.applyTheme && applyTheme(\(json))")
    }

    func webView(_ w: WKWebView, didFinish n: WKNavigation!) { applyTheme() }

    func load() { web.load(URLRequest(url: widgetURL, cachePolicy: .reloadIgnoringLocalCacheData)) }

    // The vault server may still be starting at login; keep retrying.
    func webView(_ w: WKWebView, didFailProvisionalNavigation n: WKNavigation!, withError e: Error) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { self.load() }
    }
    func webView(_ w: WKWebView, didFail n: WKNavigation!, withError e: Error) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { self.load() }
    }
    func webViewWebContentProcessDidTerminate(_ w: WKWebView) { load() }

    @objc func reload() { load() }
    @objc func openVault() { onOpenGallery() }
    @objc func hideFromMenu() { hide(); onHide() }
}
