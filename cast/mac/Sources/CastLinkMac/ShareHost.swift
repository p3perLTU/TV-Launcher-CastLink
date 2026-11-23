// Sharing this Mac's screen with a Windows PC, the way cast/pc/Share.cs does it: while the
// user has "Share screen" on, this Mac waits on UDP 47802; the PC that wants the picture finds it
// (SHARE_WHO -> SHARE_NAME) and asks for it (PULL). The Mac then casts to that PC's port 47800
// exactly as to a TV: the same encryption, and the pairing code is confirmed the first time.
//
// Accepted only from private IPv4 addresses, PULL at most twice a second.

import CastLinkCore
import Darwin
import Foundation
import SystemConfiguration

final class ShareHost {
    private var fd: Int32 = -1
    private var running = false
    private var lastPull: Int64 = 0
    private let lock = NSLock()

    /// a viewer asked for the picture: its address and name (called on the network thread)
    var onPull: ((String, String) -> Void)?

    /// this Mac's name as others see it (System Settings -> General -> Sharing)
    static func computerName() -> String {
        if let n = SCDynamicStoreCopyComputerName(nil, nil) as String?, !n.isEmpty { return n }
        return Host.current().localizedName ?? "Mac"
    }

    /// starts listening; false when the port is taken (another CastLink already shares)
    func start() -> Bool {
        let s = socket(AF_INET, SOCK_DGRAM, 0)
        if s < 0 { return false }
        var tv = timeval(tv_sec: 0, tv_usec: 300_000)
        setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = Proto.sharePort.bigEndian
        let ok = withUnsafePointer(to: &addr) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(s, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        if ok != 0 { close(s); return false }
        lock.lock(); fd = s; running = true; lock.unlock()
        let t = Thread { [weak self] in self?.loop(s) }
        t.name = "castlink share"
        t.start()
        return true
    }

    func stop() {
        lock.lock()
        running = false
        let s = fd
        fd = -1
        lock.unlock()
        if s >= 0 { close(s) }
    }

    private var isRunning: Bool { lock.lock(); defer { lock.unlock() }; return running }

    private func loop(_ s: Int32) {
        var buf = [UInt8](repeating: 0, count: 300)
        while isRunning {
            var from = sockaddr_in()
            var len = socklen_t(MemoryLayout<sockaddr_in>.size)
            let n = withUnsafeMutablePointer(to: &from) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(s, &buf, buf.count, 0, $0, &len) } }
            if n < 1 { continue }
            let ipBytes = withUnsafeBytes(of: from.sin_addr.s_addr) { Array($0) }
            if !Web.isLanV4(ipBytes) { continue }
            let pkt = Array(buf[0..<n])
            switch pkt[0] {
            case Proto.shareWho:
                let answer = Proto.textPacket(Proto.shareName, ShareHost.computerName())
                _ = answer.withUnsafeBytes { a in
                    withUnsafePointer(to: &from) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { sendto(s, a.baseAddress, a.count, 0, $0, len) } }
                }
            case Proto.pull:
                let now = TvCast.nowMs()
                if now - lastPull < 500 { continue }
                lastPull = now
                let ip = ipBytes.map(String.init).joined(separator: ".")
                onPull?(ip, Proto.packetText(pkt) ?? ip)
            default:
                break
            }
        }
    }
}
