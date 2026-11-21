// The page server: serves cast/pc/web/ipad.html and streams the picture, sound and touch input
// over one WebSocket, exactly like cast/pc/WebCast.cs (the WebSocket path; WebRTC is answered
// with "rtc-off", so the page stays on the WebSocket).
//
//   http://<this Mac>:47900/     the page
//   ws://<this Mac>:47900/ws     the stream and the input
//
// Security as on Windows: only private-network clients, the Host header must be one of our
// LAN addresses (no DNS rebinding), the WebSocket must come from our own page (Origin), the
// page must send the 6-digit code (at most one try every 2 s, 5 wrong codes lock for 5
// minutes and make a new code), at most 24 connections, one viewer at a time.

import CastLinkCore
import Darwin
import Foundation

final class PageServer {
    let page: Data
    var allowControl: Bool
    private(set) var code: String
    /// called when the viewer wants a keyframe (new viewer, frames dropped, page went visible)
    var keyRequest: (() -> Void)?
    var onInput: ((InputEvent) -> Void)?
    var onRelease: (() -> Void)?
    var onCodeChanged: ((String) -> Void)?
    enum Event {
        case connected(String), left(String), slow, replaced, wrongCode(String), locked(String), page(String)
    }
    var onEvent: ((Event) -> Void)?
    /// the viewer's address, or nil
    private(set) var viewer: String?

    private var listenFd: Int32 = -1
    private let gate = NSLock()
    private let throttle = AuthThrottle()
    private var active: WebClient?
    private var connections = 0

    // stream state (encoder and audio threads)
    private var sps: [UInt8]?, pps: [UInt8]?
    var width = 0, height = 0, audioRate = 48000

    init(page: Data, code: String?, allowControl: Bool) {
        self.page = page
        self.allowControl = allowControl
        self.code = code.map { Web.isCode($0) ? $0 : PageServer.newCode() } ?? PageServer.newCode()
    }

    static func newCode() -> String {
        var g = SystemRandomNumberGenerator()
        return String(format: "%06ld", Int.random(in: 0..<1_000_000, using: &g))
    }

    static func nowMs() -> Int64 { return Int64(DispatchTime.now().uptimeNanoseconds / 1_000_000) }

    // ---- listening --------------------------------------------------------------------------

    func start() throws {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        if fd < 0 { throw POSIXError(.EIO) }
        var one: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, socklen_t(MemoryLayout<Int32>.size))
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = Web.port.bigEndian
        addr.sin_addr.s_addr = INADDR_ANY
        let ok = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        if ok != 0 || listen(fd, 16) != 0 {
            close(fd)
            throw NSError(domain: "CastLink", code: 1, userInfo: [NSLocalizedDescriptionKey: "port \(Web.port) is busy (is CastLink already running?)"])
        }
        listenFd = fd
        let t = Thread { [weak self] in self?.acceptLoop() }
        t.name = "accept"
        t.start()
    }

    /// stops listening and closes the viewer
    func stop() {
        if listenFd >= 0 { shutdown(listenFd, SHUT_RDWR); close(listenFd); listenFd = -1 }
        gate.lock()
        let c = active
        active = nil
        gate.unlock()
        if let c = c { c.closedByUs = true; c.close() }
    }

    private func acceptLoop() {
        while true {
            var from = sockaddr_in()
            var len = socklen_t(MemoryLayout<sockaddr_in>.size)
            let fd = listenFd
            if fd < 0 { return }
            let c = withUnsafeMutablePointer(to: &from) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { accept(fd, $0, &len) }
            }
            if c < 0 { if errno == EINTR { continue }; return }
            let ip = withUnsafeBytes(of: from.sin_addr.s_addr) { Array($0) }
            if !Web.isLoopbackV4(ip) && !Web.isLanV4(ip) { close(c); continue }
            gate.lock()
            if connections >= Web.maxConnections { gate.unlock(); close(c); continue }
            connections += 1
            gate.unlock()
            let addr = ip.map(String.init).joined(separator: ".")
            let t = Thread { [weak self] in
                guard let self = self else { close(c); return }
                let conn = Conn(fd: c)
                self.serve(conn, from: addr)
                conn.close()
                self.gate.lock(); self.connections -= 1; self.gate.unlock()
            }
            t.name = "web client"
            t.start()
        }
    }

    // ---- one HTTP request -------------------------------------------------------------------

    private func serve(_ c: Conn, from: String) {
        c.setTimeouts(seconds: 5)
        if c.peekFirst() == 0x16 {
            // a TLS handshake: the https page needs the PC version's certificate authority,
            // not made here yet. The page also works over http (no wake lock).
            return
        }
        guard let head = c.readHead(max: Web.maxHeader) else { return }
        var h: [String: String] = [:]
        guard let path = Web.parseRequest(head, &h) else { return }
        if !Web.isOwnAddress(h["host"]) { return } // a name (DNS rebinding) or someone else's address
        if path == "/manifest.webmanifest" {
            c.respond("200 OK", "application/manifest+json", Data(PageServer.manifest.utf8))
            return
        }
        if path == "/" || path.hasPrefix("/?") {
            c.respond("200 OK", "text/html; charset=utf-8", page)
            return
        }
        guard path == "/ws", let key = h["sec-websocket-key"], h["upgrade"]?.lowercased() == "websocket",
              Web.sameOrigin(h["origin"], h["host"]) else {
            c.respond("404 Not Found", "text/plain", Data("not found".utf8))
            return
        }
        let resp = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "
            + Web.webSocketAccept(key) + "\r\n\r\n"
        if !c.write(Array(resp.utf8)) { return }
        runSocket(WebClient(conn: c, from: from))
    }

    static let manifest = "{\"name\":\"CastLink\",\"short_name\":\"CastLink\",\"start_url\":\"/\",\"scope\":\"/\","
        + "\"display\":\"standalone\",\"background_color\":\"#000000\",\"theme_color\":\"#000000\"}"

    private func runSocket(_ c: WebClient) {
        c.conn.setTimeouts(seconds: 20)
        guard let hello = c.readMessage(), hello.0 == 2, hello.1.count == 7, hello.1[0] == 0x10 else { return }
        let msg = hello.1
        var ok = false, newCode = false
        var wait: Int64 = 0
        gate.lock()
        let now = PageServer.nowMs()
        wait = throttle.waitMs(now)
        ok = wait == 0 && codeMatches(msg)
        if wait == 0 && !ok && throttle.fail(now) { code = PageServer.newCode(); newCode = true }
        if ok { throttle.success() }
        gate.unlock()
        if !ok {
            // a guess while locked is not even compared, so it tells nothing about the code
            c.sendNow(1, Array((wait > 0 ? "{\"t\":\"locked\",\"s\":\((wait + 999) / 1000)}" : "{\"t\":\"badcode\"}").utf8))
            if wait == 0 { onEvent?(newCode ? .locked(c.from) : .wrongCode(c.from)) }
            if newCode { onCodeChanged?(code) }
            return
        }
        gate.lock()
        let old = active
        active = c
        gate.unlock()
        // one viewer at a time: the newest wins, the old page is told so it does not reconnect
        if let old = old { old.closedByUs = true; old.sendText("{\"t\":\"replaced\"}"); old.close() }
        c.conn.setTimeouts(seconds: 0)
        c.startSender()
        c.sendText("{\"t\":\"ok\"}")
        viewer = c.from
        onEvent?(.connected(c.from))
        c.ready = true
        keyRequest?()
        defer {
            onRelease?() // a drag cut off by the Wi-Fi must not leave a button held
            gate.lock()
            if active === c { active = nil; viewer = nil }
            gate.unlock()
            onEvent?(c.writeFailed ? .slow : c.closedByUs ? .replaced : .left(c.from))
            c.close()
        }
        while let m = c.readMessage() {
            let (op, msg) = m
            if op == 1 {
                let t = String(decoding: msg, as: UTF8.self)
                if t.hasPrefix("{") {
                    // WebRTC signalling: not available on the Mac, the page stays on the WebSocket
                    if t.contains("\"rtc-hello\"") { c.sendText("{\"t\":\"rtc-off\"}") }
                    continue
                }
                if !t.hasPrefix("video:") && !t.hasPrefix("WebRTC") { onEvent?(.page(String(t.prefix(300)))) } // a problem reported by the page
                continue
            }
            if msg.isEmpty { continue }
            if msg[0] == 0x11 { c.skipToKey = true; keyRequest?(); continue }
            if !allowControl { continue }
            if let e = InputDecoder.decode(msg) { onInput?(e) }
        }
    }

    private func codeMatches(_ msg: [UInt8]) -> Bool {
        let want = Array(code.utf8)
        var diff: UInt8 = 0
        for i in 0..<6 { diff |= msg[1 + i] ^ want[i] }
        return diff == 0
    }

    // ---- the stream -------------------------------------------------------------------------

    /// One encoded frame: AVCC (4-byte lengths) as VideoToolbox gives it, with the stream's
    /// parameter sets on keyframes. pts in microseconds.
    func video(_ avcc: [UInt8], key: Bool, ptsUs: Int64, sps newSps: [UInt8]?, pps newPps: [UInt8]?) {
        if key, let s = newSps, let p = newPps { gate.lock(); sps = s; pps = p; gate.unlock() }
        gate.lock()
        let c = active, s = sps, p = pps
        gate.unlock()
        guard let c = c, c.ready else { return }
        if !c.initSent {
            // a viewer starts with the init segment and a keyframe
            guard key, let s = s, let p = p else { keyRequest?(); return }
            c.sendText("{\"t\":\"config\",\"codec\":\"\(Fmp4.codecString(s))\",\"w\":\(width),\"h\":\(height),\"rate\":\(audioRate),\"control\":\(allowControl)}")
            c.sendBinary(0x01, Fmp4.initSegment(sps: s, pps: p, width: width, height: height))
            c.initSent = true
        }
        let duration = Fmp4.frameDuration(lastPts: c.lastPts, pts: ptsUs)
        c.lastPts = ptsUs
        if c.skipToKey && !key { return }
        let sample = Fmp4.toLengthPrefixed(Fmp4.splitLengthPrefixed(avcc))
        c.sequence &+= 1
        let frag = Fmp4.fragment(sequence: c.sequence, decodeTime: c.decodeTime, duration: duration, key: key, sample: sample)
        c.decodeTime += UInt64(duration)
        if !c.enqueueFrame(frag, key: key) { keyRequest?() }
    }

    /// 16-bit little-endian stereo PCM at audioRate.
    func audio(_ pcm: [UInt8]) {
        gate.lock()
        let c = active
        gate.unlock()
        guard let c = c, c.initSent else { return }
        c.addAudio(pcm, chunk: audioRate / 50 * 4) // 20 ms per message
    }

    var viewerConnected: Bool { gate.lock(); defer { gate.unlock() }; return active != nil }

    // ---- addresses --------------------------------------------------------------------------

    /// This Mac's private IPv4 addresses, the Wi-Fi / Ethernet ones first.
    static func lanAddresses() -> [String] {
        var out: [(String, String)] = []
        var ifap: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifap) == 0, let first = ifap else { return [] }
        defer { freeifaddrs(ifap) }
        var p: UnsafeMutablePointer<ifaddrs>? = first
        while let i = p {
            defer { p = i.pointee.ifa_next }
            guard let sa = i.pointee.ifa_addr, sa.pointee.sa_family == sa_family_t(AF_INET),
                  i.pointee.ifa_flags & UInt32(IFF_UP) != 0 else { continue }
            let a = sa.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee.sin_addr.s_addr }
            let b = withUnsafeBytes(of: a) { Array($0) }
            if Web.isLanV4(b) { out.append((String(cString: i.pointee.ifa_name), b.map(String.init).joined(separator: "."))) }
        }
        return out.sorted { ($0.0.hasPrefix("en") ? 0 : 1, $0.0) < ($1.0.hasPrefix("en") ? 0 : 1, $1.0) }.map { $0.1 }
    }
}

// ---- a socket ---------------------------------------------------------------------------------

final class Conn {
    let fd: Int32
    private var buf: [UInt8] = []
    private let closeLock = NSLock()
    private var closed = false

    init(fd: Int32) {
        self.fd = fd
        var one: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, socklen_t(MemoryLayout<Int32>.size))
    }

    func setTimeouts(seconds: Int) {
        var tv = timeval(tv_sec: seconds, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        var sv = timeval(tv_sec: 5, tv_usec: 0) // a send stalled for 5 s ends the connection
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &sv, socklen_t(MemoryLayout<timeval>.size))
    }

    func peekFirst() -> Int {
        var b: UInt8 = 0
        return recv(fd, &b, 1, MSG_PEEK) == 1 ? Int(b) : -1
    }

    /// exactly n bytes, or nil when the connection ended
    func read(_ n: Int) -> [UInt8]? {
        while buf.count < n {
            var tmp = [UInt8](repeating: 0, count: 16384)
            let r = recv(fd, &tmp, tmp.count, 0)
            if r <= 0 { if r < 0 && errno == EINTR { continue }; return nil }
            buf.append(contentsOf: tmp[0..<r])
        }
        let out = Array(buf[0..<n])
        buf.removeFirst(n)
        return out
    }

    /// the request head up to the empty line, or nil if longer than max or cut off
    func readHead(max: Int) -> String? {
        var head: [UInt8] = []
        while head.count < 4 || head[head.count - 4] != 13 || head[head.count - 3] != 10 || head[head.count - 2] != 13 || head[head.count - 1] != 10 {
            if head.count >= max { return nil }
            guard let b = read(1) else { return nil }
            head.append(b[0])
        }
        return String(decoding: head, as: UTF8.self)
    }

    func write(_ b: [UInt8]) -> Bool {
        var off = 0
        while off < b.count {
            let r = b.withUnsafeBytes { send(fd, $0.baseAddress! + off, b.count - off, 0) }
            if r <= 0 { if r < 0 && errno == EINTR { continue }; return false }
            off += r
        }
        return true
    }

    func respond(_ status: String, _ type: String, _ body: Data) {
        let csp = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self'; "
            + "media-src 'self' blob:; connect-src 'self' ws: wss:; manifest-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"
        let head = "HTTP/1.1 \(status)\r\nContent-Type: \(type)\r\nContent-Length: \(body.count)\r\nCache-Control: no-store\r\n"
            + "X-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer\r\n"
            + "Content-Security-Policy: \(csp)\r\nConnection: close\r\n\r\n"
        if write(Array(head.utf8)) { _ = write(Array(body)) }
    }

    func close() {
        closeLock.lock(); defer { closeLock.unlock() }
        if closed { return }
        closed = true
        shutdown(fd, SHUT_RDWR)
        Darwin.close(fd)
    }
}

// ---- one viewer -------------------------------------------------------------------------------

final class WebClient {
    let conn: Conn
    let from: String
    private let cond = NSCondition()
    private var queue: [[UInt8]] = []
    private var queuedFrames = 0
    private var closed = false
    private var audioBuf: [UInt8] = []

    var writeFailed = false, closedByUs = false
    var ready = false, initSent = false
    /// frames were dropped: send nothing until the next keyframe
    var skipToKey = false
    var sequence: UInt32 = 0
    var decodeTime: UInt64 = 0
    var lastPts: Int64 = -1

    init(conn: Conn, from: String) {
        self.conn = conn
        self.from = from
    }

    func readMessage() -> (Int, [UInt8])? {
        return Web.readFrame({ [conn] n in conn.read(n) }) { [weak self] op, data in
            if op == 9 { self?.enqueue(Web.frame(10, data)) } // pong
        }
    }

    func startSender() {
        let t = Thread { [weak self] in self?.sendLoop() }
        t.name = "web send"
        t.start()
    }

    func sendText(_ t: String) { enqueue(Web.frame(1, Array(t.utf8))) }

    /// written at once, before the sender thread runs (the "wrong code" answer)
    func sendNow(_ opcode: Int, _ payload: [UInt8]) { _ = conn.write(Web.frame(opcode, payload)) }

    func sendBinary(_ type: UInt8, _ body: [UInt8]) { enqueue(Web.frame(2, [type] + body)) }

    /// false if the viewer fell behind and the backlog was dropped (see WebCast.cs)
    func enqueueFrame(_ fragment: [UInt8], key: Bool) -> Bool {
        let f = Web.frame(2, [key ? 0x04 : 0x02] + fragment)
        cond.lock(); defer { cond.unlock() }
        var ok = true
        if queuedFrames >= Web.maxQueuedFrames {
            // the Wi-Fi cannot keep up: drop the queued pictures (sound and texts stay), but keep
            // a keyframe still waiting unless a newer one is here
            var lastKey: [UInt8]?
            var keep: [[UInt8]] = []
            for q in queue {
                if !Web.isPicture(q) { keep.append(q) } else if Web.payloadType(q) == 0x04 { lastKey = q }
            }
            queue.removeAll()
            if !key, let k = lastKey { queue.append(k) }
            queue.append(contentsOf: keep)
            queuedFrames = !key && lastKey != nil ? 1 : 0
            if !key {
                skipToKey = true
                cond.signal()
                return false
            }
            ok = false
        }
        if key { skipToKey = false }
        queue.append(f)
        queuedFrames += 1
        cond.signal()
        return ok
    }

    func addAudio(_ pcm: [UInt8], chunk: Int) {
        var off = 0
        while off < pcm.count {
            let n = min(pcm.count - off, chunk - audioBuf.count)
            audioBuf.append(contentsOf: pcm[off..<(off + n)])
            off += n
            if audioBuf.count == chunk {
                cond.lock()
                let far = queue.count > 40 // far behind: sound is dropped, not delayed
                cond.unlock()
                if !far { enqueue(Web.frame(2, [0x03] + audioBuf)) }
                audioBuf.removeAll(keepingCapacity: true)
            }
        }
    }

    private func enqueue(_ frame: [UInt8]) {
        cond.lock()
        queue.append(frame)
        cond.signal()
        cond.unlock()
    }

    private func sendLoop() {
        while true {
            cond.lock()
            while queue.isEmpty && !closed { _ = cond.wait(until: Date(timeIntervalSinceNow: 0.5)) }
            if closed { cond.unlock(); return }
            let f = queue.removeFirst()
            if Web.isPicture(f) { queuedFrames -= 1 }
            cond.unlock()
            if !conn.write(f) { writeFailed = true; close(); return }
        }
    }

    func close() {
        cond.lock()
        closed = true
        cond.broadcast()
        cond.unlock()
        conn.close()
    }
}
