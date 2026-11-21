// The pure parts of the page server: HTTP request parsing, the checks against DNS rebinding and
// other sites, WebSocket framing and the pairing-code throttle. Ported from cast/pc/WebCast.cs.

import Foundation

public enum Web {
    public static let port: UInt16 = 47900
    public static let maxHeader = 8192
    public static let maxText = 16384
    public static let maxConnections = 24
    public static let maxQueuedFrames = 30

    /// GET path and lower-case headers, or nil for anything that is not a plain GET.
    public static func parseRequest(_ text: String, _ headers: inout [String: String]) -> String? {
        let lines = text.components(separatedBy: "\r\n")
        let first = lines[0].split(separator: " ", omittingEmptySubsequences: false).map(String.init)
        if first.count != 3 || first[0] != "GET" || !first[2].hasPrefix("HTTP/1.") { return nil }
        for line in lines.dropFirst() {
            guard let c = line.firstIndex(of: ":"), c != line.startIndex else { continue }
            let name = line[..<c].trimmingCharacters(in: .whitespaces).lowercased()
            headers[name] = line[line.index(after: c)...].trimmingCharacters(in: .whitespaces)
        }
        return first[1]
    }

    /// The WebSocket must be opened by our own page: Origin http(s)://host[:port] equal to Host.
    public static func sameOrigin(_ origin: String?, _ host: String?) -> Bool {
        guard let o = origin?.lowercased(), let h = host?.lowercased() else { return false }
        return o == "http://" + h || o == "https://" + h
    }

    /// 10/8, 172.16/12, 192.168/16 and link-local 169.254/16 (a USB or ad-hoc link).
    public static func isLanV4(_ a: [UInt8]) -> Bool {
        if a.count != 4 { return false }
        let b0 = a[0], b1 = a[1]
        return b0 == 10 || (b0 == 172 && b1 >= 16 && b1 <= 31) || (b0 == 192 && b1 == 168) || (b0 == 169 && b1 == 254)
    }

    public static func isLoopbackV4(_ a: [UInt8]) -> Bool { return a.count == 4 && a[0] == 127 }

    /// Dotted IPv4 to bytes; nil for short forms ("10.1"), names and out-of-range parts.
    public static func parseV4(_ s: String) -> [UInt8]? {
        let parts = s.split(separator: ".", omittingEmptySubsequences: false)
        if parts.count != 4 { return nil }
        var out: [UInt8] = []
        for p in parts {
            if p.isEmpty || p.count > 3 || !p.allSatisfy({ $0.isASCII && $0.isNumber }) { return nil }
            guard let v = Int(p), v <= 255 else { return nil }
            out.append(UInt8(v))
        }
        return out
    }

    /// The Host header must be a private or loopback IPv4 address, with or without a port. A host
    /// name would mean a website reached us through DNS rebinding: refused.
    public static func isOwnAddress(_ host: String?) -> Bool {
        guard let host = host, !host.isEmpty, host.count <= 64 else { return false }
        var ip = Substring(host)
        if let colon = host.firstIndex(of: ":") {
            ip = host[..<colon]
            guard let port = Int(host[host.index(after: colon)...]), port >= 1, port <= 65535 else { return false }
        }
        guard let a = parseV4(String(ip)) else { return false }
        return isLoopbackV4(a) || isLanV4(a)
    }

    public static func webSocketAccept(_ key: String) -> String {
        let src = Array((key.trimmingCharacters(in: .whitespaces) + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").utf8)
        return base64(sha1(src))
    }

    /// WebSocket frame header for a server message (never masked).
    public static func frameHeader(_ opcode: Int, _ length: Int) -> [UInt8] {
        let b0 = UInt8(0x80 | opcode)
        if length < 126 { return [b0, UInt8(length)] }
        if length < 65536 { return [b0, 126, UInt8(length >> 8), UInt8(length & 0xFF)] }
        var h: [UInt8] = [b0, 127]
        for i in stride(from: 56, through: 0, by: -8) { h.append(UInt8((UInt64(length) >> UInt64(i)) & 0xFF)) }
        return h
    }

    public static func frame(_ opcode: Int, _ payload: [UInt8]) -> [UInt8] {
        return frameHeader(opcode, payload.count) + payload
    }

    /// One message from the page. read(n) returns exactly n bytes or nil when the connection is
    /// over. Returns the opcode (1 text, 2 binary) with its payload, or nil: pages must mask,
    /// binary messages are at most 512 bytes and text at most 16 KB.
    public static func readFrame(_ read: (Int) -> [UInt8]?, control: ((Int, [UInt8]) -> Void)? = nil) -> (Int, [UInt8])? {
        while true {
            guard let h = read(2) else { return nil }
            let op = Int(h[0] & 0x0F)
            let fin = h[0] & 0x80 != 0, masked = h[1] & 0x80 != 0
            var len = Int(h[1] & 0x7F)
            if len == 126 {
                guard let l = read(2) else { return nil }
                len = Int(l[0]) << 8 | Int(l[1])
            } else if len == 127 {
                return nil
            }
            if !masked || !fin || len > (op == 1 ? maxText : InputDecoder.maxMessage) { return nil }
            guard let mask = read(4) else { return nil }
            var data: [UInt8] = []
            if len > 0 {
                guard let d = read(len) else { return nil }
                data = d
            }
            for i in 0..<data.count { data[i] ^= mask[i & 3] }
            if op == 8 { return nil }
            if op == 9 || op == 10 { control?(op, data); continue }
            if op != 1 && op != 2 { return nil }
            return (op, data)
        }
    }

    /// The type byte of a queued binary message (after its 2..10 byte frame header), or -1.
    public static func payloadType(_ frame: [UInt8]) -> Int {
        if frame.count < 2 { return -1 }
        let l = Int(frame[1] & 0x7F)
        let at = l < 126 ? 2 : l == 126 ? 4 : 10
        return at < frame.count ? Int(frame[at]) : -1
    }

    /// A queued picture (binary message 0x02 / 0x04): what gets dropped when the viewer is behind.
    public static func isPicture(_ frame: [UInt8]) -> Bool {
        if frame.isEmpty || frame[0] & 0x0F != 2 { return false }
        let t = payloadType(frame)
        return t == 0x02 || t == 0x04
    }

    public static func isCode(_ c: String) -> Bool {
        return c.count == 6 && c.allSatisfy { $0 >= "0" && $0 <= "9" }
    }

    // ---- SHA-1 and Base64 (only for the WebSocket handshake) ---------------------------------

    public static func sha1(_ message: [UInt8]) -> [UInt8] {
        var h0: UInt32 = 0x67452301, h1: UInt32 = 0xEFCDAB89, h2: UInt32 = 0x98BADCFE, h3: UInt32 = 0x10325476, h4: UInt32 = 0xC3D2E1F0
        var m = message
        let bitLen = UInt64(message.count) * 8
        m.append(0x80)
        while m.count % 64 != 56 { m.append(0) }
        for i in stride(from: 56, through: 0, by: -8) { m.append(UInt8((bitLen >> UInt64(i)) & 0xFF)) }
        var w = [UInt32](repeating: 0, count: 80)
        for chunk in stride(from: 0, to: m.count, by: 64) {
            for i in 0..<16 {
                let j = chunk + i * 4
                w[i] = UInt32(m[j]) << 24 | UInt32(m[j + 1]) << 16 | UInt32(m[j + 2]) << 8 | UInt32(m[j + 3])
            }
            for i in 16..<80 { w[i] = rotl(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1) }
            var a = h0, b = h1, c = h2, d = h3, e = h4
            for i in 0..<80 {
                let f: UInt32, k: UInt32
                switch i {
                case 0..<20: f = (b & c) | (~b & d); k = 0x5A827999
                case 20..<40: f = b ^ c ^ d; k = 0x6ED9EBA1
                case 40..<60: f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC
                default: f = b ^ c ^ d; k = 0xCA62C1D6
                }
                let t = rotl(a, 5) &+ f &+ e &+ k &+ w[i]
                e = d; d = c; c = rotl(b, 30); b = a; a = t
            }
            h0 = h0 &+ a; h1 = h1 &+ b; h2 = h2 &+ c; h3 = h3 &+ d; h4 = h4 &+ e
        }
        var out: [UInt8] = []
        for v in [h0, h1, h2, h3, h4] { out += [UInt8(v >> 24), UInt8((v >> 16) & 0xFF), UInt8((v >> 8) & 0xFF), UInt8(v & 0xFF)] }
        return out
    }

    static func rotl(_ v: UInt32, _ n: UInt32) -> UInt32 { return (v << n) | (v >> (32 - n)) }

    public static func base64(_ d: [UInt8]) -> String {
        let t = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/")
        var s = ""
        var i = 0
        while i < d.count {
            let b0 = Int(d[i]), b1 = i + 1 < d.count ? Int(d[i + 1]) : 0, b2 = i + 2 < d.count ? Int(d[i + 2]) : 0
            s.append(t[b0 >> 2])
            s.append(t[(b0 & 3) << 4 | b1 >> 4])
            s.append(i + 1 < d.count ? t[(b1 & 15) << 2 | b2 >> 6] : "=")
            s.append(i + 2 < d.count ? t[b2 & 63] : "=")
            i += 3
        }
        return s
    }

    /// A JSON string literal (for the few texts the server sends).
    public static func json(_ s: String) -> String {
        var o = "\""
        for u in s.unicodeScalars {
            switch u {
            case "\"": o += "\\\""
            case "\\": o += "\\\\"
            case "\n": o += "\\n"
            case "\r": o += "\\r"
            case "\t": o += "\\t"
            default:
                if u.value < 0x20 { o += String(format4: u.value) } else { o.unicodeScalars.append(u) }
            }
        }
        return o + "\""
    }
}

extension String {
    init(format4 v: UInt32) {
        let d = Array("0123456789abcdef")
        self = "\\u00" + String(d[Int(v >> 4) & 15]) + String(d[Int(v & 15)])
    }
}

/// Guessing the 6-digit code: at most one try every 2 s, and after 5 wrong codes nobody can try
/// for 5 minutes (the caller then makes a new code). Counted for all addresses together.
public final class AuthThrottle {
    public static let maxFailures = 5
    public static let delayMs: Int64 = 2000, lockMs: Int64 = 5 * 60 * 1000
    var failures = 0
    var nextTry: Int64 = 0, lockedUntil: Int64 = 0

    public init() {}

    /// 0 when a try is allowed now, else how long to wait.
    public func waitMs(_ now: Int64) -> Int64 { return max(0, max(nextTry, lockedUntil) - now) }

    /// A wrong code; true when this one started the lock (time for a new code).
    public func fail(_ now: Int64) -> Bool {
        nextTry = now + AuthThrottle.delayMs
        failures += 1
        if failures < AuthThrottle.maxFailures { return false }
        failures = 0
        lockedUntil = now + AuthThrottle.lockMs
        return true
    }

    public func success() { failures = 0 }
}
