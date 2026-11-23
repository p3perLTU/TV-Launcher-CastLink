// The CastLink wire format to the TV / phone receiver (UDP 47800, big-endian), the same as
// cast/common/.../Proto.java and the Proto class in cast/pc/TVCastPC.cs. Everything but the
// SecureLink handshake and WHO / NAME travels inside SEALED packets.

import Foundation

public enum Proto {
    public static let port: UInt16 = 47800

    public static let config: UInt8 = 1, video: UInt8 = 2, audio: UInt8 = 3, keyreq: UInt8 = 10, bye: UInt8 = 11
    public static let ping: UInt8 = 12, pong: UInt8 = 13, input: UInt8 = 14, who: UInt8 = 15, name: UInt8 = 16, info: UInt8 = 17
    /// like NAME, from a Windows PC receiver / a phone receiver
    public static let pcName: UInt8 = 18, phoneName: UInt8 = 19

    // PC -> PC sharing, in the clear on UDP 47802 (cast/pc/Share.cs): a PC that wants this Mac's
    // picture asks for it; this Mac then casts to that PC's port 47800 as to a TV
    public static let sharePort: UInt16 = 47802
    /// SHARE_WHO (viewer -> broadcast), SHARE_NAME | UTF-8 name (answer), PULL | UTF-8 viewer name
    public static let shareWho: UInt8 = 30, shareName: UInt8 = 31, pull: UInt8 = 32
    public static let configControl: UInt8 = 1
    public static let tokenBytes = 8
    public static let configLong = 14 + 1 + 8
    public static let flagKey: UInt8 = 1
    public static let videoHeader = 1 + 1 + 4 + 2 + 2 + 8
    /// keeps a packet under a 1500-byte MTU with IP + UDP headers
    public static let maxPayload = 1400
    public static let sampleRate = 48000, channels = 2
    /// 5 ms of 48 kHz stereo 16-bit
    public static let audioChunkBytes = 48 * 5 * 2 * 2

    /// VIDEO packets for a frame of len bytes, or -1 if it does not fit the count field
    public static func videoChunks(_ len: Int) -> Int {
        let count = (len + maxPayload - 1) / maxPayload
        return count > 0xFFFF ? -1 : count
    }

    public static func videoHeader(key: Bool, frame: Int32, chunk: Int, count: Int, ptsUs: Int64) -> [UInt8] {
        var b: [UInt8] = [video, key ? flagKey : 0]
        putInt(&b, frame)
        b += [UInt8((chunk >> 8) & 0xFF), UInt8(chunk & 0xFF), UInt8((count >> 8) & 0xFF), UInt8(count & 0xFF)]
        putLong(&b, ptsUs)
        return b
    }

    public static func putInt(_ b: inout [UInt8], _ v: Int32) {
        let u = UInt32(bitPattern: v)
        b += [UInt8(u >> 24), UInt8((u >> 16) & 0xFF), UInt8((u >> 8) & 0xFF), UInt8(u & 0xFF)]
    }

    public static func putLong(_ b: inout [UInt8], _ v: Int64) {
        let u = UInt64(bitPattern: v)
        for s in stride(from: 56, through: 0, by: -8) { b.append(UInt8((u >> UInt64(s)) & 0xFF)) }
    }

    public static func getInt(_ b: [UInt8], _ o: Int) -> Int32 {
        return Int32(bitPattern: UInt32(b[o]) << 24 | UInt32(b[o + 1]) << 16 | UInt32(b[o + 2]) << 8 | UInt32(b[o + 3]))
    }

    public static func getLong(_ b: [UInt8], _ o: Int) -> Int64 {
        var u: UInt64 = 0
        for i in 0..<8 { u = u << 8 | UInt64(b[o + i]) }
        return Int64(bitPattern: u)
    }

    /// an INPUT packet with this cast's token: the input message in it, else nil
    public static func inputMessage(_ pkt: [UInt8], token: [UInt8]) -> [UInt8]? {
        guard pkt.count >= 2 + tokenBytes, pkt[0] == input else { return nil }
        var d: UInt8 = 0
        for i in 0..<tokenBytes { d |= pkt[1 + i] ^ token[i] }
        return d == 0 ? Array(pkt[(1 + tokenBytes)...]) : nil
    }

    // ---- finding receivers: one mDNS PTR question for _tvcast._udp.local --------------------

    /// the question, with the "unicast reply" bit (answers come to our own port)
    public static func mdnsQuery() -> [UInt8] {
        var q: [UInt8] = [0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]
        for label in ["_tvcast", "_udp", "local"] {
            q.append(UInt8(label.utf8.count))
            q += Array(label.utf8)
        }
        q += [0, 0, 12, 0x80, 1]
        return q
    }

    /// a receiver's NAME / PCNAME / PHONENAME answer to WHO, nil if the packet is something else
    public static func nameReply(_ b: [UInt8]) -> String? {
        guard b.count >= 2, b[0] == name || b[0] == pcName || b[0] == phoneName else { return nil }
        return packetText(b)
    }

    /// type | UTF-8 text, the text cut to at most 255 bytes (on a character boundary)
    public static func textPacket(_ type: UInt8, _ text: String) -> [UInt8] {
        var t = Array(text.utf8)
        if t.count > 255 {
            t = Array(t.prefix(255))
            while !t.isEmpty, String(bytes: t, encoding: .utf8) == nil { t.removeLast() }
        }
        return [type] + t
    }

    /// the text after the type byte (at most 255 bytes), trimmed; nil when empty
    public static func packetText(_ b: [UInt8]) -> String? {
        guard b.count >= 2 else { return nil }
        let s = String(decoding: b[1..<min(b.count, 256)], as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        return s.isEmpty ? nil : s
    }

    /// frames a second to a PC receiver: as many as the shared screen shows (else the receiver's
    /// rate), 30..240 (Caster.PcFps)
    public static func pcFps(receiverHz: Int, ownHz: Int) -> Int {
        return max(30, min(240, ownHz > 0 ? ownHz : receiverHz))
    }

    /// bits a second to a PC receiver: 20 Mb/s per 60 frames of 1080p, more pixels a little more,
    /// at most 80 Mb/s (Caster.PcBitrate)
    public static func pcBitrate(fps: Int, _ w: Int, _ h: Int) -> Int {
        let k = Double(fps) / 60.0 * max(1.0, min(1.6, Double(w * h) / (1920.0 * 1080.0)))
        return Int(min(80_000_000, 20_000_000 * k))
    }

    /// the instance name of a PTR answer for _tvcast, nil for anything else (also cut or hostile packets)
    public static func instanceName(_ b: [UInt8]) -> String? {
        guard b.count >= 12 else { return nil }
        let qd = Int(b[4]) << 8 | Int(b[5]), an = Int(b[6]) << 8 | Int(b[7])
        var p = 12
        for _ in 0..<qd {
            guard readName(b, &p) != nil else { return nil }
            p += 4
        }
        for _ in 0..<an {
            guard let owner = readName(b, &p), p + 10 <= b.count else { return nil }
            let type = Int(b[p]) << 8 | Int(b[p + 1])
            let rdlen = Int(b[p + 8]) << 8 | Int(b[p + 9])
            p += 10
            if type == 12 && owner.first == "_tvcast" {
                var r = p
                if let target = readName(b, &r), let first = target.first { return first }
            }
            p += rdlen
        }
        return nil
    }

    /// a DNS name's labels (compression pointers followed at most 20 times); nil if it runs off the packet
    static func readName(_ b: [UInt8], _ p: inout Int) -> [String]? {
        var labels: [String] = []
        var pos = p, jumps = 0, jumped = false
        while true {
            guard pos < b.count else { return nil }
            let len = Int(b[pos])
            if len == 0 { pos += 1; break }
            if len & 0xC0 == 0xC0 {
                guard pos + 1 < b.count else { return nil }
                let target = (len & 0x3F) << 8 | Int(b[pos + 1])
                if !jumped { p = pos + 2 }
                jumped = true
                pos = target
                jumps += 1
                if jumps > 20 { break }
                continue
            }
            guard pos + 1 + len <= b.count else { return nil }
            labels.append(String(decoding: b[(pos + 1)..<(pos + 1 + len)], as: UTF8.self))
            pos += 1 + len
        }
        if !jumped { p = pos }
        return labels
    }

    /// the largest picture to send: the receiver's own screen when it said (INFO), else 1920x1080
    public static func sizeLimit(recvW: Int, recvH: Int) -> (Int, Int) {
        if recvW > 0 && recvH > 0 { return (min(recvW, 2560), min(recvH, 1600)) }
        return (1920, 1080)
    }

    /// the picture size for a w x h screen within the limit, aspect kept, even
    public static func fit(_ w: Int, _ h: Int, maxW: Int, maxH: Int) -> (Int, Int) {
        let s = min(1.0, min(Double(maxW) / Double(w), Double(maxH) / Double(h)))
        return (Int(Double(w) * s) & ~1, Int(Double(h) * s) & ~1)
    }

    /// more pixels need more bits: 1080p = 1x, up to 1.6x
    public static func scaledBitrate(_ base: Int, _ w: Int, _ h: Int) -> Int {
        let k = max(1.0, min(1.6, Double(w * h) / (1920.0 * 1080.0)))
        return Int(Double(base) * k)
    }

    /// AVCC (4-byte lengths, as VideoToolbox gives it) to Annex-B, with SPS / PPS before a keyframe
    public static func annexB(_ avcc: [UInt8], sps: [UInt8]?, pps: [UInt8]?) -> [UInt8] {
        let start: [UInt8] = [0, 0, 0, 1]
        var out: [UInt8] = []
        out.reserveCapacity(avcc.count + 64)
        if let s = sps, let p = pps { out += start + s + start + p }
        for n in Fmp4.splitLengthPrefixed(avcc) {
            let t = Fmp4.nalType(n)
            if t == 7 || t == 8 || t == 9 { continue }
            out += start
            out += n
        }
        return out
    }
}
