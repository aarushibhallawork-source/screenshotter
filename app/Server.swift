// A tiny HTTP server on 127.0.0.1:7333 that serves the gallery and widget pages
// and the JSON API they call. Only reachable from this Mac.
import Cocoa
import Network

let port: UInt16 = 7333
let baseURL = URL(string: "http://localhost:\(port)/")!

struct Request {
    let method: String
    let path: String
    let headers: [String: String]
    let body: Data
}

struct Response {
    var status = 200
    var body = Data()
    var type = "application/json"
    var cacheable = false

    static func json(_ obj: Any, status: Int = 200) -> Response {
        Response(status: status, body: (try? JSONSerialization.data(withJSONObject: obj)) ?? Data())
    }
    static let notFound = json(["error": "not found"], status: 404)
}

final class Server {
    private var listener: NWListener?
    private var attempts = 0
    var onOpenGallery: () -> Void = {}
    var onFatal: (String) -> Void = { _ in }

    func start() {
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        params.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!)
        guard let l = try? NWListener(using: params) else { return retry("could not create listener") }
        l.newConnectionHandler = { [weak self] c in self?.handle(c) }
        l.stateUpdateHandler = { [weak self] state in
            if case .failed(let e) = state { l.cancel(); self?.retry("\(e)") }
        }
        l.start(queue: DispatchQueue(label: "vault.http"))
        listener = l
    }

    // An older copy may still be shutting down and holding the port; give it a moment.
    private func retry(_ why: String) {
        attempts += 1
        NSLog("server start failed (\(why)), attempt \(attempts)")
        if attempts >= 10 { DispatchQueue.main.async { self.onFatal(why) }; return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self.start() }
    }

    private func handle(_ c: NWConnection) {
        c.start(queue: DispatchQueue.global(qos: .userInitiated))
        receive(c, Data())
    }

    private func receive(_ c: NWConnection, _ buffer: Data) {
        c.receive(minimumIncompleteLength: 1, maximumLength: 1 << 16) { data, _, done, err in
            var buf = buffer
            if let d = data { buf.append(d) }
            if let req = self.parse(buf) { return self.respond(c, req) }
            if done || err != nil || buf.count > 1 << 22 { return c.cancel() }
            self.receive(c, buf)
        }
    }

    private func parse(_ buf: Data) -> Request? {
        guard let end = buf.range(of: Data("\r\n\r\n".utf8)),
              let head = String(data: buf[buf.startIndex..<end.lowerBound], encoding: .utf8) else { return nil }
        var lines = head.components(separatedBy: "\r\n")
        let first = lines.removeFirst().split(separator: " ")
        guard first.count >= 2 else { return nil }
        var headers: [String: String] = [:]
        for line in lines {
            guard let i = line.firstIndex(of: ":") else { continue }
            headers[line[..<i].lowercased()] = line[line.index(after: i)...].trimmingCharacters(in: .whitespaces)
        }
        let len = Int(headers["content-length"] ?? "") ?? 0
        guard buf.endIndex - end.upperBound >= len else { return nil }
        return Request(method: String(first[0]), path: String(first[1]), headers: headers,
                       body: buf.subdata(in: end.upperBound..<(end.upperBound + len)))
    }

    private func respond(_ c: NWConnection, _ req: Request) {
        let res = route(req)
        let reason = [200: "OK", 400: "Bad Request", 403: "Forbidden", 404: "Not Found"][res.status] ?? "OK"
        let head = "HTTP/1.1 \(res.status) \(reason)\r\nContent-Type: \(res.type)\r\nContent-Length: \(res.body.count)\r\n" +
            "Cache-Control: \(res.cacheable ? "max-age=3600" : "no-store")\r\nConnection: close\r\n\r\n"
        var out = Data(head.utf8)
        out.append(res.body)
        c.send(content: out, completion: .contentProcessed { _ in c.cancel() })
    }

    // MARK: routes

    private static let types = [
        "png": "image/png", "jpg": "image/jpeg", "jpeg": "image/jpeg", "gif": "image/gif", "heic": "image/heic",
        "tif": "image/tiff", "tiff": "image/tiff", "pdf": "application/pdf", "mov": "video/quicktime", "mp4": "video/mp4",
        "html": "text/html; charset=utf-8",
    ]

    private func file(_ url: URL?, cacheable: Bool = true) -> Response {
        guard let url = url, let d = try? Data(contentsOf: url) else { return .notFound }
        return Response(body: d, type: Server.types[url.pathExtension.lowercased()] ?? "application/octet-stream",
                        cacheable: cacheable)
    }

    private func page(_ name: String) -> Response {
        file(Bundle.main.url(forResource: name, withExtension: "html"), cacheable: false)
    }

    private func route(_ req: Request) -> Response {
        let raw = String(req.path.split(separator: "?", maxSplits: 1).first ?? "")
        let path = raw.removingPercentEncoding ?? raw
        let vault = Vault.shared

        if req.method == "GET" {
            switch path {
            case "/": return page("gallery")
            case "/widget": return page("widget")
            case "/api/version": return .json(["version": vault.version])
            case "/api/list":
                let items = vault.listItems()
                let total = items.reduce(0) { $0 + ($1["size"] as? Int ?? 0) }
                return .json(["items": items, "total": total, "version": vault.version, "config": vault.config()])
            default:
                if path.hasPrefix("/files/") { return file(vault.safe(String(path.dropFirst(7)))) }
                if path.hasPrefix("/thumbs/") { return file(vault.safe(String(path.dropFirst(8)), in: Paths.thumbs)) }
                return .notFound
            }
        }

        guard req.method == "POST" else { return .notFound }
        // Only accept requests from our own pages.
        if let o = req.headers["origin"], o != "http://localhost:\(port)", o != "http://127.0.0.1:\(port)" {
            return .json(["error": "forbidden"], status: 403)
        }
        let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any] ?? [:]
        let target = (body["path"] as? String).flatMap { vault.safe($0) }

        switch path {
        case "/api/copy":
            guard let u = target else { return .notFound }
            return .json(["ok": vault.copyToClipboard(u)])
        case "/api/reveal":
            guard let u = target else { return .notFound }
            DispatchQueue.main.async { NSWorkspace.shared.activateFileViewerSelecting([u]) }
            return .json(["ok": true])
        case "/api/open":
            guard let u = target else { return .notFound }
            DispatchQueue.main.async { NSWorkspace.shared.open(u) }
            return .json(["ok": true])
        case "/api/openvault":
            DispatchQueue.main.async { self.onOpenGallery() }
            return .json(["ok": true])
        case "/api/delete":
            vault.trash(body["paths"] as? [String] ?? [])
            return .json(["ok": true])
        case "/api/config":
            return .json(["ok": true, "config": vault.setRetention((body["retention_days"] as? Int) ?? 0)])
        default:
            return .notFound
        }
    }
}
