// Fragmented MP4 for the page's Media Source / WebCodecs player: one init segment (ftyp + moov
// with the SPS/PPS), then one moof + mdat per encoded frame. A port of cast/pc/Fmp4.cs, byte for
// byte the same boxes, so the iPad page (cast/pc/web/ipad.html) plays it unchanged.

public enum Fmp4 {
    public static let timescale: UInt32 = 90000

    /// The NAL units of an Annex-B buffer (start codes 00 00 01 / 00 00 00 01 removed).
    public static func splitNals(_ b: [UInt8]) -> [ArraySlice<UInt8>] {
        var nals: [ArraySlice<UInt8>] = []
        var start = -1
        var i = 0
        let len = b.count
        while i + 2 < len {
            if b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1 {
                if start >= 0 {
                    // the zero before a 4-byte start code belongs to the start code
                    var end = i
                    while end > start && b[end - 1] == 0 { end -= 1 }
                    nals.append(b[start..<end])
                }
                i += 3
                start = i
            } else {
                i += 1
            }
        }
        if start >= 0 && start < len { nals.append(b[start..<len]) }
        return nals.filter { !$0.isEmpty }
    }

    public static func nalType<C: Collection>(_ n: C) -> Int where C.Element == UInt8 {
        return Int(n.first! & 0x1F)
    }

    /// The NAL units of an AVCC buffer (4-byte big-endian lengths), as VideoToolbox outputs them.
    public static func splitLengthPrefixed(_ b: [UInt8]) -> [ArraySlice<UInt8>] {
        var nals: [ArraySlice<UInt8>] = []
        var p = 0
        while p + 4 <= b.count {
            let n = Int(b[p]) << 24 | Int(b[p + 1]) << 16 | Int(b[p + 2]) << 8 | Int(b[p + 3])
            p += 4
            if n <= 0 || p + n > b.count { break }
            nals.append(b[p..<(p + n)])
            p += n
        }
        return nals
    }

    /// MSE codec string from an SPS, e.g. avc1.640028.
    public static func codecString(_ sps: [UInt8]) -> String {
        return "avc1." + hex2(sps[1]) + hex2(sps[2]) + hex2(sps[3])
    }

    static func hex2(_ v: UInt8) -> String {
        let d = Array("0123456789abcdef")
        return String([d[Int(v >> 4)], d[Int(v & 15)]])
    }

    /// A frame's NAL units with 4-byte lengths, without SPS / PPS / AUD (those live in the init
    /// segment or mean nothing in MP4).
    public static func toLengthPrefixed(_ nals: [ArraySlice<UInt8>]) -> [UInt8] {
        var m: [UInt8] = []
        for n in nals {
            let t = nalType(n)
            if t == 7 || t == 8 || t == 9 { continue }
            u32(&m, UInt32(n.count))
            m.append(contentsOf: n)
        }
        return m
    }

    // ---- init segment -----------------------------------------------------------------------

    public static func initSegment(sps: [UInt8], pps: [UInt8], width: Int, height: Int) -> [UInt8] {
        var m: [UInt8] = []
        box(&m, "ftyp") { s in
            ascii(&s, "iso5"); u32(&s, 512)
            ascii(&s, "iso5"); ascii(&s, "iso6"); ascii(&s, "mp41"); ascii(&s, "avc1")
        }
        box(&m, "moov") { moov in
            full(&moov, "mvhd", 0, 0) { s in
                u32(&s, 0); u32(&s, 0); u32(&s, 1000); u32(&s, 0)
                u32(&s, 0x00010000); u16(&s, 0x0100); zero(&s, 10)
                matrix(&s)
                zero(&s, 24)
                u32(&s, 2)
            }
            box(&moov, "trak") { trak in
                full(&trak, "tkhd", 0, 3) { s in
                    u32(&s, 0); u32(&s, 0); u32(&s, 1); u32(&s, 0); u32(&s, 0)
                    zero(&s, 8); u16(&s, 0); u16(&s, 0); u16(&s, 0); u16(&s, 0)
                    matrix(&s)
                    u32(&s, UInt32(width) << 16); u32(&s, UInt32(height) << 16)
                }
                box(&trak, "mdia") { mdia in
                    full(&mdia, "mdhd", 0, 0) { s in
                        u32(&s, 0); u32(&s, 0); u32(&s, timescale); u32(&s, 0)
                        u16(&s, 0x55C4); u16(&s, 0)
                    }
                    full(&mdia, "hdlr", 0, 0) { s in
                        u32(&s, 0); ascii(&s, "vide"); zero(&s, 12); ascii(&s, "video"); s.append(0)
                    }
                    box(&mdia, "minf") { minf in
                        full(&minf, "vmhd", 0, 1) { s in zero(&s, 8) }
                        box(&minf, "dinf") { dinf in
                            full(&dinf, "dref", 0, 0) { s in
                                u32(&s, 1)
                                full(&s, "url ", 0, 1) { _ in }
                            }
                        }
                        box(&minf, "stbl") { stbl in
                            full(&stbl, "stsd", 0, 0) { s in
                                u32(&s, 1)
                                box(&s, "avc1") { a in
                                    zero(&a, 6); u16(&a, 1)
                                    zero(&a, 16)
                                    u16(&a, UInt16(width)); u16(&a, UInt16(height))
                                    u32(&a, 0x00480000); u32(&a, 0x00480000)
                                    u32(&a, 0); u16(&a, 1)
                                    zero(&a, 32)
                                    u16(&a, 0x18); u16(&a, 0xFFFF)
                                    box(&a, "avcC") { c in avcC(&c, sps, pps) }
                                }
                            }
                            full(&stbl, "stts", 0, 0) { s in u32(&s, 0) }
                            full(&stbl, "stsc", 0, 0) { s in u32(&s, 0) }
                            full(&stbl, "stsz", 0, 0) { s in u32(&s, 0); u32(&s, 0) }
                            full(&stbl, "stco", 0, 0) { s in u32(&s, 0) }
                        }
                    }
                }
            }
            box(&moov, "mvex") { mvex in
                full(&mvex, "trex", 0, 0) { s in
                    u32(&s, 1); u32(&s, 1); u32(&s, 0); u32(&s, 0); u32(&s, 0)
                }
            }
        }
        return m
    }

    /// The avcC record alone (also what WebCodecs takes as "description").
    public static func avcC(_ c: inout [UInt8], _ sps: [UInt8], _ pps: [UInt8]) {
        c.append(1)
        c.append(sps[1]); c.append(sps[2]); c.append(sps[3])
        c.append(0xFF)
        c.append(0xE1)
        u16(&c, UInt16(sps.count)); c.append(contentsOf: sps)
        c.append(1)
        u16(&c, UInt16(pps.count)); c.append(contentsOf: pps)
        if sps[1] == 100 || sps[1] == 110 || sps[1] == 122 || sps[1] == 144 {
            c.append(contentsOf: [0xFD, 0xF8, 0xF8, 0])
        }
    }

    // ---- one frame --------------------------------------------------------------------------

    /// moof + mdat for one frame. decodeTime and duration are in timescale units.
    public static func fragment(sequence: UInt32, decodeTime: UInt64, duration: UInt32, key: Bool, sample: [UInt8]) -> [UInt8] {
        var moof: [UInt8] = []
        box(&moof, "moof") { m in
            full(&m, "mfhd", 0, 0) { s in u32(&s, sequence) }
            box(&m, "traf") { traf in
                full(&traf, "tfhd", 0, 0x020000) { s in u32(&s, 1) }
                full(&traf, "tfdt", 1, 0) { s in u64(&s, decodeTime) }
                full(&traf, "trun", 0, 0x000701) { s in
                    u32(&s, 1)
                    u32(&s, 0) // data offset, patched below
                    u32(&s, duration)
                    u32(&s, UInt32(sample.count))
                    u32(&s, key ? 0x02000000 : 0x01010000)
                }
            }
        }
        let dataOffset = UInt32(moof.count + 8)
        let at = trunDataOffset(moof)
        put32(&moof, at, dataOffset)
        var out = moof
        out.reserveCapacity(moof.count + 8 + sample.count)
        u32(&out, UInt32(8 + sample.count))
        ascii(&out, "mdat")
        out.append(contentsOf: sample)
        return out
    }

    // trun: size(4) type(4) version+flags(4) sample_count(4) data_offset(4)
    static func trunDataOffset(_ b: [UInt8]) -> Int {
        let t: [UInt8] = Array("trun".utf8)
        var i = 0
        while i + 4 <= b.count {
            if b[i] == t[0] && b[i + 1] == t[1] && b[i + 2] == t[2] && b[i + 3] == t[3] { return i + 12 }
            i += 1
        }
        fatalError("no trun")
    }

    // ---- box writing ------------------------------------------------------------------------

    static func box(_ outer: inout [UInt8], _ type: String, _ body: (inout [UInt8]) -> Void) {
        var b: [UInt8] = []
        body(&b)
        u32(&outer, UInt32(8 + b.count))
        ascii(&outer, type)
        outer.append(contentsOf: b)
    }

    static func full(_ outer: inout [UInt8], _ type: String, _ version: Int, _ flags: Int, _ body: (inout [UInt8]) -> Void) {
        box(&outer, type) { s in
            u32(&s, UInt32(version << 24 | flags))
            body(&s)
        }
    }

    static func matrix(_ s: inout [UInt8]) {
        for v: UInt32 in [0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000] { u32(&s, v) }
    }

    static func ascii(_ s: inout [UInt8], _ t: String) { s.append(contentsOf: Array(t.utf8)) }
    static func zero(_ s: inout [UInt8], _ n: Int) { s.append(contentsOf: [UInt8](repeating: 0, count: n)) }
    static func u16(_ s: inout [UInt8], _ v: UInt16) { s.append(UInt8(v >> 8)); s.append(UInt8(v & 0xFF)) }
    static func u32(_ s: inout [UInt8], _ v: UInt32) {
        s.append(UInt8(v >> 24)); s.append(UInt8((v >> 16) & 0xFF)); s.append(UInt8((v >> 8) & 0xFF)); s.append(UInt8(v & 0xFF))
    }
    static func u64(_ s: inout [UInt8], _ v: UInt64) { u32(&s, UInt32(v >> 32)); u32(&s, UInt32(v & 0xFFFF_FFFF)) }
    static func put32(_ b: inout [UInt8], _ o: Int, _ v: UInt32) {
        b[o] = UInt8(v >> 24); b[o + 1] = UInt8((v >> 16) & 0xFF); b[o + 2] = UInt8((v >> 8) & 0xFF); b[o + 3] = UInt8(v & 0xFF)
    }

    /// How long a frame stays on screen, in timescale units: the real time since the previous
    /// frame (capture pts, microseconds), 1/120 s .. 0.1 s (see WebCast.FrameDuration).
    public static func frameDuration(lastPts: Int64, pts: Int64) -> UInt32 {
        if lastPts < 0 { return timescale / 60 }
        let us = max(1_000_000 / 120, min(100_000, pts - lastPts))
        return UInt32((us * Int64(timescale) + 500_000) / 1_000_000)
    }
}
