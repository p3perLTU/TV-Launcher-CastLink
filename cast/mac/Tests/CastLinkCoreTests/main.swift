// Tests for CastLinkCore, the same cases as tests/cs/Tests.cs for the Windows version.
// A plain executable (the Command Line Tools have no XCTest): swift run CastLinkCoreTests

import CastLinkCore
import Foundation

var failed = 0, passed = 0

func equal<T: Equatable>(_ expected: T, _ actual: T, _ what: String) {
    if expected == actual { passed += 1 } else { failed += 1; print("FAIL \(what): expected \(expected), got \(actual)") }
}

func check(_ v: Bool, _ what: String) { equal(true, v, what) }

func hex<C: Collection>(_ b: C) -> String where C.Element == UInt8 {
    let d = Array("0123456789abcdef")
    return String(b.flatMap { [d[Int($0 >> 4)], d[Int($0 & 15)]] })
}

func be32(_ b: [UInt8], _ o: Int) -> Int { return Int(b[o]) << 24 | Int(b[o + 1]) << 16 | Int(b[o + 2]) << 8 | Int(b[o + 3]) }

/// top-level boxes as (type, size); counts a failure if the sizes do not add up
func boxes(_ b: [UInt8], _ from: Int, _ to: Int) -> [(String, Int)] {
    var r: [(String, Int)] = []
    var p = from
    while p < to {
        let size = be32(b, p)
        if size < 8 || p + size > to { check(false, "box size at \(p)"); return r }
        r.append((String(decoding: b[(p + 4)..<(p + 8)], as: UTF8.self), size))
        p += size
    }
    equal(to, p, "boxes fill the range exactly")
    return r
}

func find(_ b: [UInt8], _ type: String, _ from: Int = 0) -> Int {
    let t = Array(type.utf8)
    var i = from + 4
    while i + 4 <= b.count {
        if b[i] == t[0] && b[i + 1] == t[1] && b[i + 2] == t[2] && b[i + 3] == t[3] { return i - 4 }
        i += 1
    }
    return -1
}

let sps: [UInt8] = [0x67, 0x64, 0x00, 0x28, 0xAC, 0xD9, 0x40, 0x78]
let pps: [UInt8] = [0x68, 0xEB, 0xE3, 0xCB]

func annexBKeyframe(_ slice: [UInt8]) -> [UInt8] {
    return [0, 0, 0, 1, 0x09, 0xF0] + [0, 0, 0, 1] + sps + [0, 0, 1] + pps + [0, 0, 0, 1] + slice
}

// ---- fMP4 ---------------------------------------------------------------------------------

do {
    let slice: [UInt8] = [0x65, 0x88, 0x80, 0x10, 0x00, 0x03, 0x01]
    let n = Fmp4.splitNals(annexBKeyframe(slice))
    equal(4, n.count, "AUD, SPS, PPS, slice")
    equal(9, Fmp4.nalType(n[0]), "AUD")
    equal(hex(sps), hex(n[1]), "SPS without its start code or the next one's zero")
    equal(hex(pps), hex(n[2]), "PPS after a 3-byte start code")
    equal(hex(slice), hex(n[3]), "slice to the end")
}
do {
    let slice: [UInt8] = [0x65, 1, 2, 3, 4]
    let s = Fmp4.toLengthPrefixed(Fmp4.splitNals(annexBKeyframe(slice)))
    equal("00000005" + hex(slice), hex(s), "only the slice, with a 4-byte length")
    let avcc: [UInt8] = [0, 0, 0, 4] + sps.prefix(4) + [0, 0, 0, 5] + slice
    let nals = Fmp4.splitLengthPrefixed(avcc)
    equal(2, nals.count, "AVCC split")
    equal("00000005" + hex(slice), hex(Fmp4.toLengthPrefixed(nals)), "AVCC: parameter sets dropped")
}
equal("avc1.640028", Fmp4.codecString(sps), "High profile, level 4.0")
do {
    let initSeg = Fmp4.initSegment(sps: sps, pps: pps, width: 1920, height: 1080)
    let top = boxes(initSeg, 0, initSeg.count)
    equal("ftyp moov", top[0].0 + " " + top[1].0, "top-level boxes")
    let moov = top[0].1
    let inMoov = boxes(initSeg, moov + 8, moov + top[1].1)
    equal("mvhd trak mvex", inMoov.map { $0.0 }.joined(separator: " "), "inside moov")
    let avcC = find(initSeg, "avcC")
    check(avcC > 0, "avcC present")
    equal(1, Int(initSeg[avcC + 8]), "avcC version")
    equal(0x64, Int(initSeg[avcC + 9]), "profile from the SPS")
    equal(0xFF, Int(initSeg[avcC + 12]), "4-byte NAL lengths")
    equal(0xE1, Int(initSeg[avcC + 13]), "one SPS")
    let avc1 = find(initSeg, "avc1", find(initSeg, "stsd"))
    equal(1920, Int(initSeg[avc1 + 32]) << 8 | Int(initSeg[avc1 + 33]), "width in avc1")
    equal(1080, Int(initSeg[avc1 + 34]) << 8 | Int(initSeg[avc1 + 35]), "height in avc1")
}
do {
    let sample: [UInt8] = [0, 0, 0, 3, 0x65, 0xAA, 0xBB]
    let f = Fmp4.fragment(sequence: 7, decodeTime: 3000, duration: 1500, key: true, sample: sample)
    let top = boxes(f, 0, f.count)
    equal("moof mdat", top[0].0 + " " + top[1].0, "moof + mdat")
    let trun = find(f, "trun")
    let dataOffset = be32(f, trun + 16)
    equal(hex(sample), hex(f[dataOffset..<(dataOffset + sample.count)]), "trun data offset points at the sample")
    equal("02000000", hex(f[(trun + 28)..<(trun + 32)]), "keyframe: sync sample")
    equal("000005dc", hex(f[(trun + 20)..<(trun + 24)]), "duration 1500")
    let tfdt = find(f, "tfdt")
    equal("0000000000000bb8", hex(f[(tfdt + 12)..<(tfdt + 20)]), "decode time 3000")
    let p = Fmp4.fragment(sequence: 8, decodeTime: 4500, duration: 1500, key: false, sample: sample)
    let pt = find(p, "trun")
    equal("01010000", hex(p[(pt + 28)..<(pt + 32)]), "other frames: not sync")
}
equal(UInt32(1500), Fmp4.frameDuration(lastPts: -1, pts: 123), "first frame: 1/60 s")
equal(UInt32(1500), Fmp4.frameDuration(lastPts: 1000000, pts: 1016667), "60 fps")
equal(UInt32(2250), Fmp4.frameDuration(lastPts: 1000000, pts: 1025000), "40 fps")
equal(UInt32(750), Fmp4.frameDuration(lastPts: 1000000, pts: 1001000), "at most 120 fps")
equal(UInt32(9000), Fmp4.frameDuration(lastPts: 1000000, pts: 6000000), "a still screen counts at most 0.1 s")
equal(UInt32(750), Fmp4.frameDuration(lastPts: 1000000, pts: 900000), "time going backwards")

// ---- HTTP and WebSocket -------------------------------------------------------------------

do {
    var h: [String: String] = [:]
    equal("/ws", Web.parseRequest("GET /ws HTTP/1.1\r\nHost: 10.0.0.2:47900\r\nUpgrade: websocket\r\n\r\n", &h) ?? "nil", "path")
    equal("10.0.0.2:47900", h["host"] ?? "nil", "header names are lower case")
    var h2: [String: String] = [:]
    equal("nil", Web.parseRequest("POST / HTTP/1.1\r\n\r\n", &h2) ?? "nil", "only GET")
    equal("nil", Web.parseRequest("GET /\r\n\r\n", &h2) ?? "nil", "HTTP/0.9 style refused")
}
check(Web.sameOrigin("http://10.0.0.2:47900", "10.0.0.2:47900"), "our own page")
check(Web.sameOrigin("https://10.0.0.2:47900", "10.0.0.2:47900"), "our own page over HTTPS")
check(!Web.sameOrigin("https://evil.example", "10.0.0.2:47900"), "another HTTPS site")
check(!Web.sameOrigin(nil, "10.0.0.2:47900"), "no Origin header")
equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", Web.webSocketAccept("dGhlIHNhbXBsZSBub25jZQ=="), "accept key (RFC 6455)")
equal("a9993e364706816aba3e25717850c26c9cd0d89d", hex(Web.sha1(Array("abc".utf8))), "SHA-1 of abc")
equal("8205", hex(Web.frameHeader(2, 5)), "short")
equal("827e0100", hex(Web.frameHeader(2, 256)), "16-bit length")
equal("827f0000000000010000", hex(Web.frameHeader(2, 65536)), "64-bit length")
check(Web.isOwnAddress("192.168.1.20:47900"), "LAN address with port")
check(Web.isOwnAddress("192.168.137.1"), "hotspot address without port")
check(Web.isOwnAddress("127.0.0.1:47900"), "this computer")
check(!Web.isOwnAddress("evil.example:47900"), "a name: DNS rebinding")
check(!Web.isOwnAddress("localhost:47900"), "names are refused, also localhost")
check(!Web.isOwnAddress("8.8.8.8:47900"), "an internet address")
check(!Web.isOwnAddress("10.1:47900"), "short IP forms")
check(!Web.isOwnAddress("192.168.1.20:99999"), "bad port")
check(!Web.isOwnAddress("[::1]:47900"), "IPv6")
check(!Web.isOwnAddress(nil) && !Web.isOwnAddress(""), "no Host header")

func clientFrame(_ op: Int, _ payload: [UInt8], _ mask: Bool) -> [UInt8] {
    var m: [UInt8] = [UInt8(0x80 | op)]
    let key: [UInt8] = [1, 2, 3, 4]
    if payload.count < 126 { m.append(UInt8((mask ? 0x80 : 0) | payload.count)) }
    else { m += [UInt8((mask ? 0x80 : 0) | 126), UInt8(payload.count >> 8), UInt8(payload.count & 0xFF)] }
    if mask { m += key }
    for i in 0..<payload.count { m.append(payload[i] ^ (mask ? key[i & 3] : 0)) }
    return m
}

func reader(_ b: [UInt8]) -> (Int) -> [UInt8]? {
    var p = 0
    return { n in
        if p + n > b.count { return nil }
        defer { p += n }
        return Array(b[p..<(p + n)])
    }
}

do {
    let hello: [UInt8] = [0x10] + Array("123456".utf8)
    var pings = 0
    let r = Web.readFrame(reader(clientFrame(9, [7], true) + clientFrame(2, hello, true))) { _, _ in pings += 1 }
    equal(2, r?.0 ?? -1, "binary after a ping")
    equal(1, pings, "ping handed to the callback")
    equal(hex(hello), hex(r?.1 ?? []), "unmasked payload")
    check(Web.readFrame(reader(clientFrame(2, hello, false))) == nil, "unmasked frames from a page are refused")
    check(Web.readFrame(reader(clientFrame(2, [UInt8](repeating: 0, count: 600), true))) == nil, "over 512 bytes refused")
    check(Web.readFrame(reader(clientFrame(8, [], true))) == nil, "close")
    check(Web.readFrame(reader([0x82])) == nil, "cut off")
    equal(1, Web.readFrame(reader(clientFrame(1, [UInt8](repeating: 0x61, count: 3000), true)))?.0 ?? -1, "a 3 KB text message is read")
    check(Web.readFrame(reader(clientFrame(2, [UInt8](repeating: 0, count: 3000), true))) == nil, "binary input stays limited to 512 bytes")
}
do {
    let pic = Web.frame(2, [0x04, 1, 2])
    check(Web.isPicture(pic) && Web.payloadType(pic) == 4, "a queued keyframe is a picture")
    check(!Web.isPicture(Web.frame(2, [0x03, 0, 0])), "sound is not a picture")
    check(!Web.isPicture(Web.frame(1, Array("{}".utf8))), "text is not a picture")
    check(Web.isPicture(Web.frame(2, [0x02] + [UInt8](repeating: 0, count: 70000))), "a big frame (64-bit length) is a picture")
}

// ---- the pairing code ---------------------------------------------------------------------

do {
    let t = AuthThrottle()
    var now: Int64 = 1000000
    equal(Int64(0), t.waitMs(now), "the first try is allowed")
    check(!t.fail(now), "one wrong code does not lock")
    equal(Int64(2000), t.waitMs(now), "2 s until the next try")
    equal(Int64(0), t.waitMs(now + 2000), "allowed again after 2 s")
    for i in 2...4 { now += 2000; check(!t.fail(now), "wrong code \(i)") }
    now += 2000
    check(t.fail(now), "the 5th wrong code locks")
    equal(Int64(5 * 60 * 1000), t.waitMs(now), "locked for 5 minutes")
    equal(Int64(0), t.waitMs(now + 5 * 60 * 1000), "open again after 5 minutes")
    now += 5 * 60 * 1000
    _ = t.fail(now); t.success()
    now += 2000
    for i in 1...4 { check(!t.fail(now), "after a success the count starts over \(i)"); now += 2000 }
}
check(Web.isCode("012345") && !Web.isCode("12345") && !Web.isCode("12345a"), "pairing code format")

// ---- input --------------------------------------------------------------------------------

do {
    var e = InputDecoder.decode([1, 0x80, 0x00, 0xFF, 0xFF])!
    check(e.kind == .move && e.x == 0x8000 && e.y == 0xFFFF, "move")
    e = InputDecoder.decode([2, 1, 1, 0, 10, 0, 20])!
    check(e.kind == .button && e.button == 1 && e.down && e.x == 10 && e.y == 20, "right button down")
    e = InputDecoder.decode([3, 0xFF, 0x88, 0x7F, 0xFF])!
    check(e.wheelY == -120 && e.wheelX == 1200, "one notch down, clamped to 10 notches")
    e = InputDecoder.decode([4] + Array("ąčę ok".utf8))!
    equal("ąčę ok", e.text, "UTF-8 text")
    e = InputDecoder.decode([5, 0x0D])!
    equal(UInt8(0x0D), e.key, "Enter")
    e = InputDecoder.decode([6, 0xFF, 0xF6, 0, 25])!
    check(e.kind == .moveBy && e.x == -10 && e.y == 25, "moveBy")
    e = InputDecoder.decode([6, 0x7F, 0xFF, 0x80, 0x00])!
    check(e.x == 2000 && e.y == -2000, "a huge jump is clamped")
    e = InputDecoder.decode([7, 0, 1])!
    check(e.kind == .buttonHere && e.button == 0 && e.down, "left down where the pointer is")
    e = InputDecoder.decode([8, 1, 0, 0x80, 0, 0x40, 0])!
    check(e.kind == .clickAt && e.button == 1 && !e.down && e.x == 0x8000 && e.y == 0x4000, "click-at")
    e = InputDecoder.decode([9, 0xFF, 0x88, 0, 0, 0, 10, 0, 20])!
    check(e.kind == .wheelAt && e.wheelY == -120 && e.x == 10 && e.y == 20, "wheel-at")
    check(InputDecoder.decode([8, 0, 1, 0, 0]) == nil, "short click-at")
    check(InputDecoder.decode([5, 0x5B]) == nil, "Windows key not allowed")
    check(InputDecoder.decode([5, 0x12]) == nil, "Alt not allowed")
    check(InputDecoder.decode([2, 3, 1, 0, 0, 0, 0]) == nil, "no button 3")
    check(InputDecoder.decode([1, 0, 0, 0]) == nil, "short move")
    check(InputDecoder.decode([4, 0x61, 0x0D]) == nil, "control characters only as KEY")
    check(InputDecoder.decode([4, 0xC3]) == nil, "broken UTF-8")
    check(InputDecoder.decode([UInt8](repeating: 0, count: 600)) == nil, "too long")
    check(InputDecoder.decode([13]) == nil, "unknown type")
    check(InputDecoder.decode([9]) == nil, "short wheel-at")
    e = InputDecoder.decode([10, 0x41, 1, 0])!
    check(e.kind == .keyRaw && e.key == 0x41 && e.down && !e.extended, "raw key A down")
    e = InputDecoder.decode([10, 0xA3, 0, 1])!
    check(e.kind == .keyRaw && e.key == 0xA3 && !e.down && e.extended, "raw right Ctrl up, extended")
    check(InputDecoder.decode([10, 0, 1, 0]) == nil, "raw vk 0 is no key")
    check(InputDecoder.decode([10, 0xFF, 1, 0]) == nil, "raw vk 0xFF is no key")
    check(InputDecoder.decode([10, 0x41, 2, 0]) == nil, "raw key: down is 0 or 1")
    check(InputDecoder.decode([10, 0x41, 1]) == nil, "short raw key")
    e = InputDecoder.decode([11, 0xFF, 0xFF, 0, 0])!
    check(e.kind == .moveTo && e.x == 65535 && e.y == 0, "move-to")
    check(InputDecoder.decode([11, 0, 0, 0]) == nil, "short move-to")
    e = InputDecoder.decode([12, 0x7F, 0xFF, 0xFF, 0x88])!
    check(e.kind == .wheelHere && e.wheelY == 1200 && e.wheelX == -120, "wheel-here, at most 10 notches")
    check(InputDecoder.decode([6, 0, 1]) == nil, "short move-by")
    check(InputDecoder.decode([7, 3, 1]) == nil, "no button 3 here either")
    let p = InputDecoder.toScreen(65535, 0, left: 1440, top: -100, width: 1080, height: 810)
    check(p.0 == 1440 + 1079 && p.1 == -100, "touch lands on the shown screen (right of the main one)")
}

// ---- raw keys from a PC receiver ---------------------------------------------------------
equal(0x00, KeyMap.macKey(vk: 0x41, extended: false) ?? 0xFFFF, "A")
equal(0x06, KeyMap.macKey(vk: 0x5A, extended: false) ?? 0xFFFF, "Z")
equal(0x1D, KeyMap.macKey(vk: 0x30, extended: false) ?? 0xFFFF, "0")
equal(0x7A, KeyMap.macKey(vk: 0x70, extended: false) ?? 0xFFFF, "F1")
equal(0x6F, KeyMap.macKey(vk: 0x7B, extended: false) ?? 0xFFFF, "F12")
equal(0x37, KeyMap.macKey(vk: 0x5B, extended: true) ?? 0xFFFF, "Win is Command")
equal(0x3B, KeyMap.macKey(vk: 0x11, extended: false) ?? 0xFFFF, "Ctrl is Control")
equal(0x3E, KeyMap.macKey(vk: 0x11, extended: true) ?? 0xFFFF, "right Ctrl")
equal(0x3A, KeyMap.macKey(vk: 0x12, extended: false) ?? 0xFFFF, "Alt is Option")
equal(0x4C, KeyMap.macKey(vk: 0x0D, extended: true) ?? 0xFFFF, "keypad Enter")
equal(0x24, KeyMap.macKey(vk: 0x0D, extended: false) ?? 0xFFFF, "Enter")
equal(0x75, KeyMap.macKey(vk: 0x2E, extended: true) ?? 0xFFFF, "Delete is Forward Delete")
equal(0x7B, KeyMap.macKey(vk: 0x25, extended: true) ?? 0xFFFF, "Left")
check(KeyMap.macKey(vk: 0x5D, extended: true) == nil, "the menu key has no Mac match")
check(KeyMap.modifier(0x37) == .command && KeyMap.modifier(0x3C) == .shift && KeyMap.modifier(0x00) == nil, "modifiers")
check(KeyMap.isArrow(0x7E) && !KeyMap.isArrow(0x24) && KeyMap.isFunctionKey(0x7A), "arrows and Fn keys")

// ---- the encrypted link (golden values from tests/cs/Tests.cs, the same as the Java tests) ----

func unhex(_ h: String) -> [UInt8] {
    let c = Array(h.utf8)
    return stride(from: 0, to: c.count, by: 2).map { UInt8(String(decoding: c[$0..<($0 + 2)], as: UTF8.self), radix: 16)! }
}

let csD = "9f26144dc9b4dfcaa1b200317802e2f2df1489e229a7833ccfbf32a4176c8261"
let csP = "0407fad123bbb8703eb1f58fe49ab0fdb034ce8eb7bb04a0ca6c9ed476299c98ceb09584ebd8d64d91f2751cdb45175a2b5e2bdac3d2254738b9754156ac448192"
let ceD = "d60cb8efff853209590f197e676d557aca0c03bbb5c2b870d4644503912a68b2"
let ceP = "0412b4821b12940bc8e49a3cf9bc7b172009dc4acf29cc6fa950ac6386e0c1bee603ecc09c51a594796617dbe19d52eec973162e957d54269a0ddaa9f61774995b"
let rsD = "bb940d30f42070930ca28c30ce22915e088ced671013b5c31eabb2e3e240e243"
let rsP = "047351cec262be63c1ad19777b7220c3ab8ca0502d935eba90a8705fa952278f829c826fbeeb3f74ee1c65479fd14dab33e983c61e0ff9d401a9078e0288a9774c"
let reD = "0507654ec1f2617ea46ebe02b882370f6b5ee3ea83feb1862176cd8ffb533975"
let reP = "042ea76c9200d27811b79427e038048c231c1a221baec3a54a19f0bd4d8833b38bcf3d481cf44046e4257c6d0dd5c47c8beb2e52eb29d812e4d5610c7fe2ffeacc"

func key(_ d: String) -> SecureLink.PrivateKey { return try! SecureLink.PrivateKey(rawRepresentation: unhex(d)) }

func linkSide(_ caster: Bool, _ casterStatic: String) -> SecureLink.Session? {
    let cn = (0..<16).map { UInt8($0 + 1) }, rn = (0..<16).map { UInt8(0xA0 + $0) }
    return try? SecureLink.derive(caster: caster, myStatic: key(caster ? csD : rsD), myEphemeral: key(caster ? ceD : reD),
                                  casterNonce: cn, receiverNonce: rn, casterEph: unhex(ceP), receiverEph: unhex(reP),
                                  casterStatic: unhex(casterStatic), receiverStatic: unhex(rsP))
}

do {
    equal(csP, hex(SecureLink.pointOf(key(csD))), "the private key gives its point")
    if let c = linkSide(true, csP), let r = linkSide(false, csP) {
        equal(982123, c.code, "caster's pairing code (Windows and Java: the same)")
        equal(982123, r.code, "receiver's pairing code")
        equal("982 123", c.codeText, "as shown")
        equal("160000000000000000c508a0bb51cae79c1dbcd6dcfa9b1d386d3f57513a49f3d6", hex(c.seal(Array("CastLink".utf8))), "caster -> receiver packet 0, byte for byte")
        equal("1600000000000000006cf7ce25eb5050ef0243bdac9a8c0e6838646306acf9b213", hex(r.seal(Array("CastLink".utf8))), "receiver -> caster packet 0")
    } else { check(false, "derive") }
    if let c = linkSide(true, csP), let r = linkSide(false, csP) {
        let p0 = c.seal(Array("one".utf8)), p1 = c.seal(Array("two".utf8))
        equal("two", String(decoding: r.open(p1) ?? [], as: UTF8.self), "packet 1")
        equal("one", String(decoding: r.open(p0) ?? [], as: UTF8.self), "packet 0 late: fine")
        check(r.open(p0) == nil, "packet 0 again: replay")
        var p2 = c.seal(Array("three".utf8))
        p2[12] ^= 1
        check(r.open(p2) == nil, "a changed byte")
        check(c.open(c.seal(Array("x".utf8))) == nil, "its own direction: other key")
    }
    let other = SecureLink.pointOf(SecureLink.PrivateKey())
    if let c = linkSide(true, csP), let r = linkSide(false, hex(other)) {
        check(r.open(c.seal(Array("hi".utf8))) == nil, "someone in the middle with another key")
    }
    check(SecureLink.onCurve(unhex(csP)), "a real point")
    var bad = unhex(csP)
    bad[64] ^= 1
    check(!SecureLink.onCurve(bad), "one bit changed: off the curve")
    check(!SecureLink.onCurve([UInt8](repeating: 0, count: 65)), "zeros")
    check((try? SecureLink.agree(key(csD), bad)) == nil, "no ECDH with such a point")

    let h = SecureLink.makeHello(nonce: [UInt8](repeating: 0, count: 16), ephemeral: unhex(ceP), staticKey: unhex(csP), name: "PC")
    let p = SecureLink.parse(h)
    check(p != nil && SecureLink.same(p?.staticKey, unhex(csP)) && p?.name == "PC", "HELLO round trip")
    check(SecureLink.parse(Array(h.prefix(SecureLink.helloFixed - 1))) == nil, "cut into the fixed part")
    var ok = [UInt8](repeating: 0, count: SecureLink.helloOkFixed + 2)
    ok[0] = SecureLink.helloOk; ok[1] = SecureLink.version
    ok.replaceSubrange(18..<83, with: unhex(reP))
    ok.replaceSubrange(83..<148, with: unhex(rsP))
    ok[SecureLink.helloOkFixed - 1] = 1
    ok[SecureLink.helloOkFixed] = UInt8(ascii: "T"); ok[SecureLink.helloOkFixed + 1] = UInt8(ascii: "V")
    let o = SecureLink.parse(ok)
    check(o != nil && o!.knowsYou && o!.name == "TV" && SecureLink.same(o!.staticKey, unhex(rsP)), "HELLO_OK fields")
}

// ---- the TV protocol ----------------------------------------------------------------------

equal("0201010203040005000711223344556677" + "88", hex(Proto.videoHeader(key: true, frame: 0x01020304, chunk: 5, count: 7, ptsUs: 0x1122334455667788)), "VIDEO header as Java")
for v: Int64 in [0, 1, -1, Int64.max, Int64.min, 0xFFFFFFFF] {
    var b: [UInt8] = []
    Proto.putLong(&b, v)
    equal(v, Proto.getLong(b, 0), "long \(v)")
}
equal(0, Proto.videoChunks(0), "empty")
equal(1, Proto.videoChunks(Proto.maxPayload), "one payload")
equal(2, Proto.videoChunks(Proto.maxPayload + 1), "one byte more")
equal(-1, Proto.videoChunks(0xFFFF * Proto.maxPayload + 1), "too large")
equal(Proto.configLong, 23, "CONFIG with the control flag and the token")
equal(Proto.audioChunkBytes, 960, "5 ms of sound")

func label(_ s: inout [UInt8], _ l: String) { s.append(UInt8(l.utf8.count)); s += Array(l.utf8) }

func ptrAnswer(_ service: String, _ name: String) -> [UInt8] {
    var s: [UInt8] = [0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0]
    label(&s, service); label(&s, "_udp"); label(&s, "local"); s.append(0)
    s += [0, 12, 0, 1, 0, 0, 0x11, 0x94]
    let rdlen = 1 + name.utf8.count + 2
    s += [UInt8(rdlen >> 8), UInt8(rdlen & 0xFF)]
    label(&s, name)
    s += [0xC0, 12]
    return s
}

equal("Living Room TV", Proto.instanceName(ptrAnswer("_tvcast", "Living Room TV")) ?? "nil", "TV name from mDNS")
check(Proto.instanceName(ptrAnswer("_googlecast", "Chromecast")) == nil, "other service")
do {
    let full = ptrAnswer("_tvcast", "TV")
    for n in 0..<full.count { _ = Proto.instanceName(Array(full.prefix(n))) } // must not crash
    var loop = full
    loop[12] = 0xC0; loop[13] = 12
    _ = Proto.instanceName(loop) // must return, not hang
    check(true, "cut and looping mDNS packets")
}
equal("Living room", Proto.nameReply([Proto.name] + Array(" Living room ".utf8)) ?? "nil", "NAME answer")
equal("Office PC", Proto.nameReply([Proto.pcName] + Array("Office PC".utf8)) ?? "nil", "PCNAME answer")
equal("Phone", Proto.nameReply([Proto.phoneName] + Array("Phone".utf8)) ?? "nil", "PHONENAME answer")
check(Proto.nameReply([Proto.shareName] + Array("x".utf8)) == nil, "SHARE_NAME is not a receiver's name")
check(Proto.nameReply([Proto.pcName]) == nil, "an empty name")
equal([Proto.shareName] + Array("Mac".utf8), Proto.textPacket(Proto.shareName, "Mac"), "SHARE_NAME packet")
equal(256, Proto.textPacket(Proto.pull, String(repeating: "a", count: 400)).count, "a name is cut to 255 bytes")
check(String(bytes: Proto.textPacket(Proto.pull, String(repeating: "ą", count: 200)).dropFirst(), encoding: .utf8) != nil, "cut on a character boundary")
equal("Desk PC", Proto.packetText([Proto.pull] + Array(" Desk PC ".utf8)) ?? "nil", "PULL viewer name")
check(Proto.packetText([Proto.pull]) == nil, "PULL without a name")
equal(144, Proto.pcFps(receiverHz: 60, ownHz: 144), "a PC receiver gets the shared screen's rate")
equal(60, Proto.pcFps(receiverHz: 60, ownHz: 0), "the receiver's rate when the screen does not say")
equal(240, Proto.pcFps(receiverHz: 60, ownHz: 360), "at most 240")
equal(30, Proto.pcFps(receiverHz: 24, ownHz: 0), "at least 30")
equal(20_000_000, Proto.pcBitrate(fps: 60, 1920, 1080), "20 Mb/s for 1080p60")
equal(48_000_000, Proto.pcBitrate(fps: 144, 1920, 1080), "more frames, more bits")
equal(80_000_000, Proto.pcBitrate(fps: 240, 2560, 1600), "at most 80 Mb/s")
check(Proto.inputMessage([Proto.input, 1, 2, 3, 4, 5, 6, 7, 8, 5, 0x0D], token: [1, 2, 3, 4, 5, 6, 7, 8]) == [5, 0x0D], "INPUT with the token")
check(Proto.inputMessage([Proto.input, 1, 2, 3, 4, 5, 6, 7, 9, 5, 0x0D], token: [1, 2, 3, 4, 5, 6, 7, 8]) == nil, "INPUT with another token")
check(Proto.fit(2560, 1600, maxW: 1920, maxH: 1080) == (1728, 1080), "a 16:10 screen to a 1080p TV")
check(Proto.fit(2160, 1620, maxW: 2340, maxH: 1080) == (1440, 1080), "an iPad-sized screen to a phone")
equal("000000016764002800000001" + "68eb" + "0000000165aa", hex(Proto.annexB([0, 0, 0, 1, 0x68, 0, 0, 0, 2, 0x65, 0xAA], sps: [0x67, 0x64, 0x00, 0x28], pps: [0x68, 0xEB])), "Annex-B keyframe: SPS, PPS, the slice (the AVCC PPS not twice)")

print("\(passed) passed, \(failed) failed")
if failed > 0 { exit(1) }
