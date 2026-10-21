// The vault: watches the screenshot inbox, copies each new capture to the clipboard,
// files it under archive/YYYY-MM/YYYY-MM-DD with a readable name, and manages the archive.
import Cocoa
import ImageIO
import UniformTypeIdentifiers

enum Paths {
    static let home = FileManager.default.homeDirectoryForCurrentUser
    static let root = home.appendingPathComponent("Library/Application Support/ScreenshotVault")
    static let inbox = root.appendingPathComponent("inbox")
    static let archive = root.appendingPathComponent("archive")
    static let thumbs = root.appendingPathComponent("thumbs")
    static let config = root.appendingPathComponent("config.json")
}

let imageExt: Set<String> = ["png", "jpg", "jpeg", "heic", "tiff", "tif", "gif"]
let videoExt: Set<String> = ["mov", "mp4"]
let mediaExt = imageExt.union(videoExt).union(["pdf"])

final class Vault {
    static let shared = Vault()
    private let fm = FileManager.default
    private let queue = DispatchQueue(label: "vault.watch", qos: .userInitiated)
    private let lock = NSLock()
    private var _version = 0

    var version: Int { lock.lock(); defer { lock.unlock() }; return _version }
    func bump() { lock.lock(); _version += 1; lock.unlock() }

    func prepare() {
        for d in [Paths.inbox, Paths.archive, Paths.thumbs] {
            try? fm.createDirectory(at: d, withIntermediateDirectories: true)
        }
    }

    // MARK: config

    func config() -> [String: Any] {
        guard let d = try? Data(contentsOf: Paths.config),
              let c = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { return ["retention_days": 0] }
        return c
    }

    func setRetention(_ days: Int) -> [String: Any] {
        var c = config()
        c["retention_days"] = max(0, days)
        if let d = try? JSONSerialization.data(withJSONObject: c) { try? d.write(to: Paths.config) }
        return c
    }

    // MARK: naming

    // "Screenshot 2026-09-15 at 3.36.22 PM" / "Screen Recording 2026-09-15 at 15.36.22"
    private static let macName = try! NSRegularExpression(
        pattern: #"(\d{4}-\d{2}-\d{2}) at (\d{1,2})\.(\d{2})\.(\d{2})[\s ]*([AP]M)?"#, options: [.caseInsensitive])
    private static let vaultName = try! NSRegularExpression(
        pattern: #"^(\d{4}-\d{2}-\d{2}) at (\d{2})\.(\d{2})\.(\d{2})(?: - (.*?))?(?: \(\d+\))?$"#)

    private static func formatter(_ f: String) -> DateFormatter {
        let d = DateFormatter()
        d.locale = Locale(identifier: "en_US_POSIX")
        d.dateFormat = f
        return d
    }
    private static let stamp = formatter("yyyy-MM-dd HH:mm:ss")
    private static let monthF = formatter("yyyy-MM")
    private static let dayF = formatter("yyyy-MM-dd")
    private static let baseF = formatter("yyyy-MM-dd 'at' HH.mm.ss")

    private static func groups(_ re: NSRegularExpression, _ s: String) -> [String?]? {
        let ns = s as NSString
        guard let m = re.firstMatch(in: s, range: NSRange(location: 0, length: ns.length)) else { return nil }
        return (0..<m.numberOfRanges).map { i in
            let r = m.range(at: i)
            return r.location == NSNotFound ? nil : ns.substring(with: r)
        }
    }

    func captureTime(_ url: URL) -> Date {
        if let g = Vault.groups(Vault.macName, url.deletingPathExtension().lastPathComponent),
           let day = g[1], var h = Int(g[2] ?? ""), let mi = g[3], let s = g[4] {
            if let ap = g[5]?.uppercased() { h = h % 12 + (ap == "PM" ? 12 : 0) }
            if let d = Vault.stamp.date(from: "\(day) \(String(format: "%02d", h)):\(mi):\(s)") { return d }
        }
        let a = try? fm.attributesOfItem(atPath: url.path)
        return (a?[.creationDate] as? Date) ?? (a?[.modificationDate] as? Date) ?? Date()
    }

    private func cleanLabel(_ s: String) -> String {
        let bad = CharacterSet(charactersIn: "/:\\").union(.controlCharacters)
        let cleaned = s.unicodeScalars.map { bad.contains($0) ? "-" : String($0) }.joined()
        return String(cleaned.trimmingCharacters(in: CharacterSet(charactersIn: " .-")).prefix(60))
    }

    private func archiveURL(_ when: Date, _ label: String, _ ext: String) -> URL {
        let dir = Paths.archive.appendingPathComponent(Vault.monthF.string(from: when))
            .appendingPathComponent(Vault.dayF.string(from: when))
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        var base = Vault.baseF.string(from: when)
        if !label.isEmpty { base += " - \(label)" }
        var dest = dir.appendingPathComponent("\(base).\(ext)")
        var n = 2
        while fm.fileExists(atPath: dest.path) {
            dest = dir.appendingPathComponent("\(base) (\(n)).\(ext)")
            n += 1
        }
        return dest
    }

    // MARK: paths

    func rel(_ url: URL) -> String {
        String(url.standardizedFileURL.path.dropFirst(Paths.archive.standardizedFileURL.path.count + 1))
    }

    /// Resolves a relative path inside `base`, refusing anything that escapes it.
    func safe(_ rel: String, in base: URL = Paths.archive) -> URL? {
        let root = base.resolvingSymlinksInPath().standardizedFileURL.path
        let u = base.appendingPathComponent(rel).resolvingSymlinksInPath().standardizedFileURL
        return u.path.hasPrefix(root + "/") ? u : nil
    }

    private func thumbURL(_ rel: String) -> URL { Paths.thumbs.appendingPathComponent(rel + ".jpg") }

    // MARK: capture handling

    private func makeThumb(_ file: URL) {
        guard imageExt.contains(file.pathExtension.lowercased()),
              let src = CGImageSourceCreateWithURL(file as CFURL, nil),
              let img = CGImageSourceCreateThumbnailAtIndex(src, 0, [
                  kCGImageSourceCreateThumbnailFromImageAlways: true,
                  kCGImageSourceCreateThumbnailWithTransform: true,
                  kCGImageSourceThumbnailMaxPixelSize: 640] as CFDictionary) else { return }
        let out = thumbURL(rel(file))
        try? fm.createDirectory(at: out.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard let dst = CGImageDestinationCreateWithURL(out as CFURL, UTType.jpeg.identifier as CFString, 1, nil) else { return }
        CGImageDestinationAddImage(dst, img, [kCGImageDestinationLossyCompressionQuality: 0.75] as CFDictionary)
        CGImageDestinationFinalize(dst)
    }

    @discardableResult
    func copyToClipboard(_ url: URL) -> Bool {
        let work = { () -> Bool in
            let pb = NSPasteboard.general
            pb.clearContents()
            let ext = url.pathExtension.lowercased()
            if imageExt.contains(ext), let img = NSImage(contentsOf: url) {
                let item = NSPasteboardItem()
                if let d = try? Data(contentsOf: url) {
                    if ext == "png" { item.setData(d, forType: .png) }
                    if ext == "jpg" || ext == "jpeg" { item.setData(d, forType: NSPasteboard.PasteboardType(UTType.jpeg.identifier)) }
                }
                if let t = img.tiffRepresentation { item.setData(t, forType: .tiff) }
                return pb.writeObjects([item])
            }
            // Recordings and PDFs: copy as a file so it pastes into Finder, Slack, Mail…
            return pb.writeObjects([url as NSURL])
        }
        return Thread.isMainThread ? work() : DispatchQueue.main.sync(execute: work)
    }

    private func isReady(_ url: URL) -> Bool {
        // macOS writes captures as hidden temp files and renames them; wait for a stable size.
        let size = { (try? self.fm.attributesOfItem(atPath: url.path)[.size] as? Int) ?? nil }
        guard let a = size(), a > 0 else { return false }
        Thread.sleep(forTimeInterval: 0.15)
        return size() == a
    }

    @discardableResult
    func ingest(_ url: URL, label: String, toClipboard: Bool) -> URL? {
        let dest = archiveURL(captureTime(url), cleanLabel(label), url.pathExtension.lowercased())
        do { try fm.moveItem(at: url, to: dest) } catch { NSLog("ingest failed: \(error)"); return nil }
        if toClipboard { copyToClipboard(dest) }
        makeThumb(dest)
        return dest
    }

    private func frontApp() -> String {
        // Read directly (thread-safe) so filing never waits on the main thread.
        NSWorkspace.shared.frontmostApplication?.localizedName ?? ""
    }

    private func mtime(_ u: URL) -> Date {
        (try? fm.attributesOfItem(atPath: u.path)[.modificationDate] as? Date) ?? .distantPast
    }

    func startWatching() {
        queue.async {
            while true {
                autoreleasepool {
                    let names = (try? self.fm.contentsOfDirectory(atPath: Paths.inbox.path)) ?? []
                    let files = names
                        .filter { !$0.hasPrefix(".") && mediaExt.contains(($0 as NSString).pathExtension.lowercased()) }
                        .map { Paths.inbox.appendingPathComponent($0) }
                        .sorted { self.mtime($0) < self.mtime($1) }
                    if !files.isEmpty {
                        let app = self.frontApp()
                        for (i, f) in files.enumerated() where self.isReady(f) {
                            if let d = self.ingest(f, label: app, toClipboard: i == files.count - 1) { NSLog("filed \(d.path)") }
                        }
                        self.bump()
                    }
                }
                Thread.sleep(forTimeInterval: 0.3)
            }
        }
        // Auto-delete, if turned on in the gallery.
        Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { _ in self.applyRetention() }
        DispatchQueue.main.asyncAfter(deadline: .now() + 30) { self.applyRetention() }
    }

    private func applyRetention() {
        // Not on `queue`: the watch loop occupies it for good.
        DispatchQueue.global(qos: .utility).async {
            let days = (self.config()["retention_days"] as? Int) ?? 0
            guard days > 0 else { return }
            let cutoff = Date().timeIntervalSince1970 - Double(days) * 86400
            let old = self.listItems().filter { ($0["ts"] as? Double ?? .infinity) < cutoff }.compactMap { $0["path"] as? String }
            if !old.isEmpty { self.trash(old); NSLog("retention removed \(old.count)") }
        }
    }

    /// Moves existing screenshots (e.g. from the Desktop) into the archive.
    func importFiles(_ urls: [URL]) -> Int {
        var n = 0
        for u in urls where mediaExt.contains(u.pathExtension.lowercased()) {
            if ingest(u, label: "", toClipboard: false) != nil { n += 1 }
        }
        bump()
        return n
    }

    func desktopScreenshots() -> [URL] {
        let desk = Paths.home.appendingPathComponent("Desktop")
        let names = (try? fm.contentsOfDirectory(atPath: desk.path)) ?? []
        return names.filter {
            ($0.hasPrefix("Screenshot ") || $0.hasPrefix("Screen Recording ")) &&
                mediaExt.contains(($0 as NSString).pathExtension.lowercased())
        }.map { desk.appendingPathComponent($0) }
    }

    // MARK: library

    func listItems() -> [[String: Any]] {
        var items: [[String: Any]] = []
        let keys: [URLResourceKey] = [.fileSizeKey, .contentModificationDateKey, .isRegularFileKey]
        guard let e = fm.enumerator(at: Paths.archive, includingPropertiesForKeys: keys) else { return [] }
        for case let url as URL in e {
            let ext = url.pathExtension.lowercased()
            guard !url.lastPathComponent.hasPrefix("."), mediaExt.contains(ext),
                  let v = try? url.resourceValues(forKeys: Set(keys)), v.isRegularFile == true else { continue }
            let r = rel(url)
            var ts = v.contentModificationDate?.timeIntervalSince1970 ?? 0
            var app = ""
            if let g = Vault.groups(Vault.vaultName, url.deletingPathExtension().lastPathComponent),
               let day = g[1], let h = g[2], let mi = g[3], let s = g[4],
               let d = Vault.stamp.date(from: "\(day) \(h):\(mi):\(s)") {
                ts = d.timeIntervalSince1970
                app = g[5] ?? ""
            }
            items.append([
                "path": r, "name": url.lastPathComponent, "app": app, "ts": ts,
                "kind": imageExt.contains(ext) ? "image" : videoExt.contains(ext) ? "video" : "file",
                "size": v.fileSize ?? 0, "thumb": fm.fileExists(atPath: thumbURL(r).path),
            ])
        }
        return items.sorted { ($0["ts"] as! Double) > ($1["ts"] as! Double) }
    }

    /// Moves files to the macOS Trash (so "Put Back" works) and tidies empty folders.
    func trash(_ rels: [String]) {
        for r in rels {
            guard let u = safe(r), fm.fileExists(atPath: u.path) else { continue }
            try? fm.trashItem(at: u, resultingItemURL: nil)
            try? fm.removeItem(at: thumbURL(r))
            var d = u.deletingLastPathComponent()
            while d.path.count > Paths.archive.path.count,
                  let c = try? fm.contentsOfDirectory(atPath: d.path), c.filter({ $0 != ".DS_Store" }).isEmpty {
                try? fm.removeItem(at: d)
                d = d.deletingLastPathComponent()
            }
        }
        bump()
    }
}
