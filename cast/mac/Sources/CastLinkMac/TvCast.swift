// "CastLink app": this Mac's screen to the CastLink receiver on a TV or an Android phone, as
// Caster in cast/pc/TVCastPC.cs does it. UDP 47800 to one receiver, encrypted (SecureLink), the
// first time confirmed with a 6-digit code; H.264 in 1400-byte chunks, 5 ms sound packets,
// keyframes on request, PING / PONG for the receiver's delay figure, INFO (the receiver's own
// screen size) and INPUT (a phone that shows this Mac controls it with touch, a TV with a mouse).

import ApplicationServices
import CastLinkCore
import CoreGraphics
import CoreMedia
import CryptoKit
import Darwin
import Foundation
import SystemConfiguration

// ---- this Mac's identity and the receivers it trusts ----------------------------------------------

enum Identity {
    static let folder = CastSession.settingsDir
    static let keyFile = folder.appendingPathComponent("identity.key")
    static let trustFile = folder.appendingPathComponent("trusted.txt")
    private static var cached: SecureLink.PrivateKey?
    private static let lock = NSLock()

    /// this Mac's static key: made once, kept readable only by this user (like ~/.ssh keys)
    static func key() -> SecureLink.PrivateKey {
        lock.lock(); defer { lock.unlock() }
        if let k = cached { return k }
        if let d = try? Data(contentsOf: keyFile), let k = try? SecureLink.PrivateKey(rawRepresentation: d) {
            cached = k
            return k
        }
        let k = SecureLink.PrivateKey()
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        FileManager.default.createFile(atPath: keyFile.path, contents: k.rawRepresentation, attributes: [.posixPermissions: 0o600])
        cached = k
        return k
    }

    /// the user confirmed this receiver's key once (the pairing code)
    static func trusted(_ point: [UInt8]) -> Bool {
        let b64 = Data(point).base64EncodedString()
        guard let text = try? String(contentsOf: trustFile, encoding: .utf8) else { return false }
        return text.split(separator: "\n").contains { $0 == b64 || $0.hasPrefix(b64 + " ") }
    }

    static func trust(_ point: [UInt8], name: String) {
        if trusted(point) { return }
        let line = Data(point).base64EncodedString() + " " + name.replacingOccurrences(of: "\n", with: " ").replacingOccurrences(of: "\r", with: " ") + "\n"
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        if let h = try? FileHandle(forWritingTo: trustFile) {
            h.seekToEndOfFile()
            h.write(Data(line.utf8))
            h.closeFile()
        } else {
            FileManager.default.createFile(atPath: trustFile.path, contents: Data(line.utf8), attributes: [.posixPermissions: 0o600])
        }
    }
}

// ---- finding receivers --------------------------------------------------------------------------

struct Receiver: Hashable {
    let name: String
    let ip: String
    var label: String { return "\(name) (\(ip))" }
}

enum Discovery {
    /// one mDNS question for _tvcast._udp.local on every network, answers for `ms`
    static func find(ms: Int = 1500) -> [Receiver] {
        let fd = socket(AF_INET, SOCK_DGRAM, 0)
        if fd < 0 { return [] }
        defer { close(fd) }
        var tv = timeval(tv_sec: 0, tv_usec: 200_000)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        var any = sockaddr_in()
        any.sin_family = sa_family_t(AF_INET)
        _ = withUnsafePointer(to: &any) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        let query = Proto.mdnsQuery()
        var mdns = sockaddr_in()
        mdns.sin_family = sa_family_t(AF_INET)
        mdns.sin_port = UInt16(5353).bigEndian
        inet_pton(AF_INET, "224.0.0.251", &mdns.sin_addr)
        let ask = {
            // on every network (Wi-Fi and Ethernet both), not only the default one
            for a in PageServer.lanAddresses() {
                var addr = in_addr()
                inet_pton(AF_INET, a, &addr)
                setsockopt(fd, IPPROTO_IP, IP_MULTICAST_IF, &addr, socklen_t(MemoryLayout<in_addr>.size))
                _ = query.withUnsafeBytes { q in
                    withUnsafePointer(to: &mdns) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { sendto(fd, q.baseAddress, q.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
                }
            }
        }
        ask()
        var found: [Receiver] = []
        let start = Date()
        var resent = false
        var buf = [UInt8](repeating: 0, count: 9000)
        while Date().timeIntervalSince(start) * 1000 < Double(ms) {
            if !resent && Date().timeIntervalSince(start) > 0.4 { ask(); resent = true }
            var from = sockaddr_in()
            var len = socklen_t(MemoryLayout<sockaddr_in>.size)
            let n = withUnsafeMutablePointer(to: &from) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(fd, &buf, buf.count, 0, $0, &len) } }
            if n <= 0 { continue }
            let pkt = Array(buf[0..<n])
            let ipBytes = withUnsafeBytes(of: from.sin_addr.s_addr) { Array($0) }
            if !Web.isLanV4(ipBytes) { continue }
            let ip = ipBytes.map(String.init).joined(separator: ".")
            let name = UInt16(bigEndian: from.sin_port) == Proto.port ? Proto.nameReply(pkt) : Proto.instanceName(pkt)
            if let name = name, !found.contains(where: { $0.ip == ip }) { found.append(Receiver(name: name, ip: ip)) }
        }
        return found
    }
}

// ---- the cast ------------------------------------------------------------------------------------

final class TvCast {
    enum State: Equatable {
        case connecting(String)
        case pairing(code: String, name: String)
        case showing(name: String)
        case oldReceiver
        case refused
    }

    var onState: ((State) -> Void)?
    var onStopped: ((String) -> Void)?
    /// a phone that shows this Mac may control it (the window's tick)
    var allowControl: Bool {
        get { lock.lock(); defer { lock.unlock() }; return _allowControl }
        set { lock.lock(); _allowControl = newValue; lock.unlock() }
    }

    let target: String
    let screenCfg: CastConfig
    let fps: Int
    let withAudio: Bool

    private let lock = NSLock()
    private var _allowControl: Bool
    private var fd: Int32 = -1
    private var on = false
    private var control: Thread?
    private var source: ScreenSource?
    private var capture: ScreenCapture?
    private var input: MacInput?
    private let token = SecureLink.randomBytes(Proto.tokenBytes)

    // the encrypted link
    private var link: SecureLink.Session?
    private var linkReady = false
    private var eph = SecureLink.PrivateKey()
    private var myNonce: [UInt8] = []
    private var helloPkt: [UInt8] = []
    private var peerStatic: [UInt8] = []
    private(set) var peerName = ""
    private var pairAnswer = 0 // 0 not yet, 1 the codes match, -1 they do not

    // the picture
    private var vw = 0, vh = 0
    private var recvW = 0, recvH = 0
    /// a PC receiver's refresh rate (INFO), 0 for a TV or phone
    private var recvHz = 0
    /// when the receiver was last heard from (any sealed packet)
    private var heardAt = TvCast.nowMs()

    /// how long the receiver has been quiet, in ms (a second viewer waits while the first answers)
    var quietMs: Int64 { lock.lock(); defer { lock.unlock() }; return TvCast.nowMs() - heardAt }
    /// the receiver is a PC (it said its refresh rate)
    var toPc: Bool { lock.lock(); defer { lock.unlock() }; return recvHz > 0 }
    private var frameId: Int32 = 0
    private let sendLock = NSLock()
    private var keyWanted = true
    private var restarting = false
    private var audioBuf: [UInt8] = []
    private var audioSeq: Int32 = 0

    // statistics for the window
    private(set) var frames = 0, bytes = 0
    var gpu: Bool { return capture?.encoder.isHardware ?? false }
    var size: (Int, Int) { return (vw, vh) }

    init(target: String, screen: CastConfig, fps: Int, audio: Bool, control: Bool) {
        self.target = target
        self.screenCfg = screen
        self.fps = fps
        self.withAudio = audio
        self._allowControl = control
    }

    static func nowMs() -> Int64 { return Int64(DispatchTime.now().uptimeNanoseconds / 1_000_000) }

    /// the clock the capture's timestamps use (for PONG: the receiver works out its delay)
    static func hostNanos() -> Int64 { return Int64(CMTimeGetSeconds(CMClockGetTime(CMClockGetHostTimeClock())) * 1e9) }

    func start() async throws {
        let fd = socket(AF_INET, SOCK_DGRAM, 0)
        if fd < 0 { throw CastError.other("socket") }
        var big: Int32 = 4 << 20
        setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &big, socklen_t(MemoryLayout<Int32>.size))
        var tv = timeval(tv_sec: 0, tv_usec: 250_000)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        var to = sockaddr_in()
        to.sin_family = sa_family_t(AF_INET)
        to.sin_port = Proto.port.bigEndian
        guard inet_pton(AF_INET, target, &to.sin_addr) == 1 else { close(fd); throw CastError.other("address") }
        let ok = withUnsafePointer(to: &to) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        if ok != 0 { close(fd); throw CastError.other("connect") }
        self.fd = fd
        on = true

        // a virtual screen takes the receiver's shape; until its INFO comes, a TV's 16:9
        let (iw, ih) = virtualSize(1920, 1080)
        let src = try await ScreenSource.prepare(virtualConfig(iw, ih))
        source = src
        if src.virtualScreen != nil { WindowMover.castStarted(src.displayID) }
        let inp = MacInput(displayID: src.displayID)
        inp.log = { TvCast.inputLog($0) }
        TvCast.inputLog("start: trusted=\(AXIsProcessTrusted()) control=\(allowControl) display=\(src.displayID) bounds=\(CGDisplayBounds(src.displayID)) virtual=\(src.virtualScreen != nil) pointer=\(CGEvent(source: nil)?.location ?? .zero) receiver=\(target):\(Proto.port) (only packets from there arrive)")
        input = inp
        try await startCapture()

        let t = Thread { [weak self] in self?.controlLoop() }
        t.name = "castlink control"
        t.start()
        control = t
    }

    private func startCapture() async throws {
        guard let src = source else { return }
        let (maxW, maxH) = Proto.sizeLimit(recvW: recvW, recvH: recvH)
        let (w, h) = Proto.fit(src.width, src.height, maxW: maxW, maxH: maxH)
        lock.lock(); let hz = recvHz; lock.unlock()
        // a PC receiver gets as many frames as the shown screen draws, with bits to match
        let ownHz = Int(CGDisplayCopyDisplayMode(src.displayID)?.refreshRate ?? 0)
        let rate = hz > 0 ? Proto.pcFps(receiverHz: hz, ownHz: ownHz) : fps
        let bitrate = hz > 0 ? Proto.pcBitrate(fps: rate, w, h) : Proto.scaledBitrate(fps > 60 ? 30_000_000 : 20_000_000, w, h)
        let cap = try ScreenCapture(displayID: src.displayID, width: w, height: h, fps: rate, audio: withAudio, bitrate: bitrate)
        cap.encoder.onFrame = { [weak self] data, key, us, sps, pps in self?.sendFrame(data, key: key, ptsUs: us, sps: sps, pps: pps) }
        cap.onAudio = { [weak self] pcm in self?.sendAudio(pcm) }
        cap.onStopped = { [weak self] e in self?.onStopped?(e.localizedDescription) }
        do {
            try await cap.start()
        } catch {
            throw CastError.capture(error.localizedDescription)
        }
        lock.lock()
        capture = cap
        vw = w; vh = h
        keyWanted = true
        lock.unlock()
        cap.requestKey()
    }

    /// the receiver told its screen size: the picture starts again at that size
    private func restartCapture() {
        lock.lock()
        if restarting { lock.unlock(); return }
        restarting = true
        let old = capture
        capture = nil
        lock.unlock()
        old?.stop()
        Task {
            await self.fitVirtualScreen()
            try? await self.startCapture()
            self.lock.lock(); self.restarting = false; self.lock.unlock()
        }
    }

    private var loggedFirstInput = false
    private static var inputLogLines = 0
    private static let inputLogLock = NSLock()

    /// ~/Library/Logs/CastLink/input.log: every INPUT from the receiver and what came of it
    private static let inputLogURL: URL = {
        let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/CastLink")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let url = dir.appendingPathComponent("input.log")
        try? Data().write(to: url) // a fresh log for each start of the app
        return url
    }()

    static func inputLog(_ s: String) {
        inputLogLock.lock(); defer { inputLogLock.unlock() }
        inputLogLines += 1
        if inputLogLines > 50_000 { return } // a few MB at most
        let f = DateFormatter(); f.dateFormat = "HH:mm:ss.SSS"
        guard let d = (f.string(from: Date()) + " " + s + "\n").data(using: .utf8),
              let h = try? FileHandle(forWritingTo: inputLogURL) else { return }
        h.seekToEndOfFile(); h.write(d); try? h.close()
    }

    /// the screen to show; a virtual one is w x h (the receiver's screen), a real one as it is
    private func virtualConfig(_ w: Int, _ h: Int) -> CastConfig {
        var c = screenCfg
        if c.displayID == nil { c.width = w & ~1; c.height = h & ~1 }
        return c
    }

    /// the virtual screen for a receiver of rw x rh: the size picked in the window if it has the
    /// receiver's shape (e.g. 4K on a 16:9 TV), else the receiver's own size
    private func virtualSize(_ rw: Int, _ rh: Int) -> (Int, Int) {
        let cw = screenCfg.width, ch = screenCfg.height
        if cw > 0 && ch > 0 && abs(Double(cw) / Double(ch) - Double(rw) / Double(rh)) < 0.01 { return (cw & ~1, ch & ~1) }
        let (maxW, maxH) = Proto.sizeLimit(recvW: rw, recvH: rh)
        return Proto.fit(rw, rh, maxW: maxW, maxH: maxH)
    }

    /// a virtual screen of another shape than the receiver's is made again in the receiver's shape
    /// (its windows may move once), so the picture fills the TV or phone without bars
    private func fitVirtualScreen() async {
        lock.lock(); let r = (recvW, recvH); lock.unlock()
        guard let old = source, old.virtualScreen != nil, r.0 > 0, r.1 > 0 else { return }
        let (w, h) = virtualSize(r.0, r.1)
        if old.width == w && old.height == h { return }
        let oldSize = (old.width, old.height), oldID = old.displayID
        source = nil // the old virtual screen goes away first
        var src = try? await ScreenSource.prepare(virtualConfig(w, h))
        if src == nil { src = try? await ScreenSource.prepare(virtualConfig(oldSize.0, oldSize.1)) }
        guard let s = src else { WindowMover.castEnding(oldID); return }
        WindowMover.replaced(oldID, by: s.displayID)
        source = s
        input?.displayID = s.displayID
        TvCast.inputLog("virtual screen made again: \(w)x\(h), display \(s.displayID) bounds \(CGDisplayBounds(s.displayID))")
    }

    func answerPairing(_ yes: Bool) {
        lock.lock(); pairAnswer = yes ? 1 : -1; lock.unlock()
    }

    func stop() {
        lock.lock()
        let wasOn = on
        on = false
        let cap = capture
        capture = nil
        lock.unlock()
        if !wasOn { return }
        cap?.stop()
        for _ in 0..<3 { sendSealed([Proto.bye]) }
        input?.releaseButtons()
        close(fd)
        fd = -1
        // its windows go to the main screen before a virtual screen goes away
        if let s = source, s.virtualScreen != nil { WindowMover.castEnding(s.displayID) }
        source = nil // a virtual screen goes away with it
    }

    // ---- sending --------------------------------------------------------------------------------

    private func sendRaw(_ b: [UInt8]) {
        let f = fd
        if f < 0 { return }
        _ = b.withUnsafeBytes { Darwin.send(f, $0.baseAddress, b.count, 0) } // the TV app may be closed for a moment
    }

    private func sendSealed(_ inner: ArraySlice<UInt8>) {
        lock.lock(); let l = link; lock.unlock()
        guard let l = l else { return }
        sendRaw(l.seal(inner))
    }

    private func sendSealed(_ inner: [UInt8]) { sendSealed(inner[...]) }

    /// a packet of the cast: sealed, and only once the receiver is confirmed
    private func send(_ inner: [UInt8]) {
        lock.lock(); let ready = linkReady; lock.unlock()
        if ready { sendSealed(inner) }
    }

    private func sendFrame(_ avcc: [UInt8], key: Bool, ptsUs: Int64, sps: [UInt8]?, pps: [UInt8]?) {
        lock.lock(); let ready = linkReady; lock.unlock()
        if !ready { return }
        let data = Proto.annexB(avcc, sps: key ? sps : nil, pps: key ? pps : nil)
        let count = Proto.videoChunks(data.count)
        if count <= 0 { return }
        sendLock.lock()
        let id = frameId
        frameId &+= 1
        for c in 0..<count {
            let o = c * Proto.maxPayload, l = min(Proto.maxPayload, data.count - o)
            var pkt = Proto.videoHeader(key: key, frame: id, chunk: c, count: count, ptsUs: ptsUs)
            pkt += data[o..<(o + l)]
            send(pkt)
        }
        sendLock.unlock()
        lock.lock(); frames += 1; bytes += data.count; lock.unlock()
    }

    /// 16-bit stereo PCM from the capture, out in 5 ms packets
    private func sendAudio(_ pcm: [UInt8]) {
        audioBuf += pcm
        while audioBuf.count >= Proto.audioChunkBytes {
            var pkt: [UInt8] = [Proto.audio]
            Proto.putInt(&pkt, audioSeq)
            audioSeq &+= 1
            pkt += audioBuf[0..<Proto.audioChunkBytes]
            audioBuf.removeFirst(Proto.audioChunkBytes)
            send(pkt)
        }
    }

    // ---- the handshake and the receiver's messages ------------------------------------------------

    /// a new handshake: new ephemeral key and nonce (also after the receiver restarted)
    private func newHello() {
        lock.lock()
        link = nil
        linkReady = false
        eph = SecureLink.PrivateKey()
        myNonce = SecureLink.randomNonce()
        helloPkt = SecureLink.makeHello(nonce: myNonce, ephemeral: SecureLink.pointOf(eph), staticKey: SecureLink.pointOf(Identity.key()),
                                        name: (SCDynamicStoreCopyComputerName(nil, nil) as String?) ?? "Mac")
        lock.unlock()
    }

    private func onHelloOk(_ b: [UInt8]) {
        guard let h = SecureLink.parse(b), h.echo != nil else { return }
        lock.lock()
        if link != nil || !SecureLink.same(h.echo, myNonce) { lock.unlock(); return }
        let me = Identity.key()
        guard let l = try? SecureLink.derive(caster: true, myStatic: me, myEphemeral: eph, casterNonce: myNonce, receiverNonce: h.nonce,
                                              casterEph: SecureLink.pointOf(eph), receiverEph: h.ephemeral,
                                              casterStatic: SecureLink.pointOf(me), receiverStatic: h.staticKey) else { lock.unlock(); return }
        peerStatic = h.staticKey
        peerName = h.name.isEmpty ? target : h.name
        link = l
        let known = Identity.trusted(h.staticKey) && h.knowsYou
        if known { linkReady = true; keyWanted = true }
        pairAnswer = 0
        lock.unlock()
        if known {
            capture?.requestKey()
            onState?(.showing(name: peerName))
        } else {
            onState?(.pairing(code: l.codeText, name: peerName))
        }
    }

    private func controlLoop() {
        newHello()
        var raw = [UInt8](repeating: 0, count: 2048)
        var lastHello: Int64 = 0, lastCfg: Int64 = 0, lastKey: Int64 = 0, lastPairShow: Int64 = 0
        var lastSealedHeard = TvCast.nowMs()
        let helloSince = TvCast.nowMs()
        var saidOld = false
        onState?(.connecting(target))
        while true {
            lock.lock()
            let running = on, l = link, ready = linkReady, answer = pairAnswer
            lock.unlock()
            if !running { return }
            let now = TvCast.nowMs()
            if l == nil {
                if now - lastHello >= 1000 {
                    lastHello = now
                    lock.lock(); let h = helloPkt; lock.unlock()
                    sendRaw(h)
                    if now - helloSince > 5000 && !saidOld { saidOld = true; onState?(.oldReceiver) } // an old receiver ignores HELLO
                }
            } else if !ready {
                if answer == 1 {
                    Identity.trust(peerStatic, name: peerName)
                    lock.lock(); linkReady = true; keyWanted = true; lock.unlock()
                    lastCfg = 0
                    capture?.requestKey()
                    onState?(.showing(name: peerName))
                } else if answer == -1 {
                    onState?(.refused)
                    stop()
                    return
                } else if now - lastPairShow >= 1000 {
                    lastPairShow = now
                    sendSealed([SecureLink.pairShow])
                }
            } else if now - lastSealedHeard > 4000 {
                newHello() // the receiver went quiet (restarted?): shake hands again, it knows us
                lastSealedHeard = now
                onState?(.connecting(target))
            }
            lock.lock(); let w = vw, h = vh, ctl = _allowControl; lock.unlock()
            if ready && now - lastCfg >= 1000 && w > 0 {
                lastCfg = now
                var cfg: [UInt8] = [Proto.config]
                Proto.putInt(&cfg, Int32(w))
                Proto.putInt(&cfg, Int32(h))
                Proto.putInt(&cfg, Int32(Proto.sampleRate))
                cfg.append(UInt8(Proto.channels))
                cfg.append((ctl ? Proto.configControl : 0) | SecureLink.configPaired)
                cfg += token
                send(cfg)
            }
            let n = recv(fd, &raw, raw.count, 0)
            if n <= 0 { continue }
            let pkt = Array(raw[0..<n])
            if pkt[0] == SecureLink.helloOk { onHelloOk(pkt); lastSealedHeard = now; continue }
            // everything else from the receiver is sealed with this cast's keys
            guard pkt[0] == SecureLink.sealed else { TvCast.inputLog("unsealed packet type \(pkt[0]) len \(n): dropped"); continue }
            guard let sl = l, let inner = sl.open(pkt), !inner.isEmpty else { TvCast.inputLog("sealed packet len \(n) did not open (link \(l == nil ? "none" : "set")): dropped"); continue }
            lastSealedHeard = now
            lock.lock(); heardAt = now; lock.unlock()
            switch inner[0] {
            case Proto.keyreq:
                if now - lastKey > 150 { lastKey = now; capture?.requestKey() }
            case Proto.input:
                // touches from a phone that shows this screen, or a TV's mouse and keyboard
                let m = Proto.inputMessage(inner, token: token)
                let e = m.flatMap { InputDecoder.decode($0) }
                if !loggedFirstInput { loggedFirstInput = true; TvCast.inputLog("first INPUT: trusted=\(AXIsProcessTrusted())") }
                if !ctl || !ready {
                    TvCast.inputLog("INPUT refused: control=\(ctl) ready=\(ready)")
                } else if m == nil {
                    TvCast.inputLog("INPUT len \(inner.count): token does not match")
                } else if let e = e {
                    input?.apply(e)
                } else {
                    TvCast.inputLog("INPUT message \(m!.prefix(12).map { String($0) }.joined(separator: " ")) not decoded")
                }
            case Proto.info where inner.count >= 9:
                let rw = Int(Proto.getInt(inner, 1)), rh = Int(Proto.getInt(inner, 5))
                var restart = false
                if rw >= 320 && rh >= 240 && rw <= 7680 && rh <= 4320 && (rw != recvW || rh != recvH) {
                    lock.lock(); recvW = rw; recvH = rh; lock.unlock()
                    restart = true
                }
                // a PC receiver adds its screen's refresh rate (TVs and phones do not)
                let hz = inner.count >= 13 ? Int(Proto.getInt(inner, 9)) : 0
                if hz >= 24 && hz <= 1000 && hz != recvHz {
                    lock.lock(); recvHz = hz; lock.unlock()
                    input?.rawKeysAllowed = true
                    TvCast.inputLog("PC receiver: \(hz) Hz, its whole keyboard is taken")
                    restart = true
                }
                if restart { restartCapture() }
            case Proto.ping where inner.count >= 9:
                var pong: [UInt8] = [Proto.pong]
                pong += inner[1..<9]
                Proto.putLong(&pong, TvCast.hostNanos())
                send(pong)
            default:
                break
            }
        }
    }

    /// frames and bytes since the last call (the window's figures)
    func takeStats() -> (Int, Int) {
        lock.lock(); defer { lock.unlock() }
        let r = (frames, bytes)
        frames = 0; bytes = 0
        return r
    }
}
