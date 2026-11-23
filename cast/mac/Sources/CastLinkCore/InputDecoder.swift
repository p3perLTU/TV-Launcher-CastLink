// Mouse and keyboard from the page: small binary messages over the WebSocket (see
// cast/pc/RemoteInput.cs for the format). decode() is pure and refuses anything malformed or
// not allowed; CastLinkMac/MacInput.swift replays the events with CGEvent.
//
//   1 MOVE    x u16, y u16                 position on the shown screen, 0..65535
//   2 BUTTON  button u8 (0 left, 1 right, 2 middle), down u8 (1/0), x u16, y u16
//   3 WHEEL   dy i16, dx i16               120 = one notch, positive = up / right
//   4 TEXT    UTF-8 text
//   5 KEY     code u8                      a Windows virtual-key code from allowedKeys
//   6 MOVEBY  dx i16, dy i16
//   7 BUTTONHERE button u8, down u8
//   8 CLICKAT button u8, down u8, x u16, y u16
//   9 WHEELAT dy i16, dx i16, x u16, y u16
//  10 KEYRAW  vk u8, down u8, extended u8  any Windows key, pressed or let go (shortcuts too);
//                                          the caller takes it only from a PC receiver
//  11 MOVETO  x u16, y u16                 the pointer goes there for real (a PC receiver's mouse)
//  12 WHEELHERE dy i16, dx i16             the wheel where the pointer is now

public enum InputKind: Int { case move = 1, button, wheel, text, key, moveBy, buttonHere, clickAt, wheelAt, keyRaw, moveTo, wheelHere }

public struct InputEvent: Equatable {
    public var kind: InputKind
    public var x = 0, y = 0          // 0..65535 (MOVEBY: pixels)
    public var button = 0            // 0 left, 1 right, 2 middle
    public var down = false
    public var wheelY = 0, wheelX = 0
    public var text = ""
    public var key: UInt8 = 0
    public var extended = false      // KEYRAW: an extended key (right Ctrl / Alt, arrows, Win, ...)

    public init(kind: InputKind) { self.kind = kind }
}

public enum InputDecoder {
    public static let maxMessage = 512

    /// Windows virtual-key codes the page may send: editing and navigation only.
    public static let allowedKeys: [UInt8] = [0x08, 0x09, 0x0D, 0x1B, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2E]

    public static func decode(_ m: [UInt8]) -> InputEvent? {
        let n = m.count
        if n < 1 || n > maxMessage { return nil }
        switch m[0] {
        case 1:
            if n != 5 { return nil }
            var e = InputEvent(kind: .move); e.x = u16(m, 1); e.y = u16(m, 3); return e
        case 2:
            if n != 7 || m[1] > 2 || m[2] > 1 { return nil }
            var e = InputEvent(kind: .button); e.button = Int(m[1]); e.down = m[2] == 1; e.x = u16(m, 3); e.y = u16(m, 5)
            return e
        case 3:
            if n != 5 { return nil }
            var e = InputEvent(kind: .wheel); e.wheelY = clamp(i16(m, 1)); e.wheelX = clamp(i16(m, 3)); return e
        case 4:
            if n < 2 { return nil }
            guard let t = String(validatingUTF8Bytes: Array(m[1...])) else { return nil }
            for u in t.unicodeScalars where u.value < 0x20 || (u.value >= 0x7F && u.value < 0xA0) { return nil }
            var e = InputEvent(kind: .text); e.text = t; return e
        case 5:
            if n != 2 || !allowedKeys.contains(m[1]) { return nil }
            var e = InputEvent(kind: .key); e.key = m[1]; return e
        case 6:
            if n != 5 { return nil }
            var e = InputEvent(kind: .moveBy); e.x = step(i16(m, 1)); e.y = step(i16(m, 3)); return e
        case 7:
            if n != 3 || m[1] > 2 || m[2] > 1 { return nil }
            var e = InputEvent(kind: .buttonHere); e.button = Int(m[1]); e.down = m[2] == 1; return e
        case 8:
            if n != 7 || m[1] > 2 || m[2] > 1 { return nil }
            var e = InputEvent(kind: .clickAt); e.button = Int(m[1]); e.down = m[2] == 1; e.x = u16(m, 3); e.y = u16(m, 5)
            return e
        case 9:
            if n != 9 { return nil }
            var e = InputEvent(kind: .wheelAt); e.wheelY = clamp(i16(m, 1)); e.wheelX = clamp(i16(m, 3)); e.x = u16(m, 5); e.y = u16(m, 7)
            return e
        case 10:
            // vk 0 and 0xFF are not keys
            if n != 4 || m[1] == 0 || m[1] == 0xFF || m[2] > 1 || m[3] > 1 { return nil }
            var e = InputEvent(kind: .keyRaw); e.key = m[1]; e.down = m[2] == 1; e.extended = m[3] == 1; return e
        case 11:
            if n != 5 { return nil }
            var e = InputEvent(kind: .moveTo); e.x = u16(m, 1); e.y = u16(m, 3); return e
        case 12:
            if n != 5 { return nil }
            var e = InputEvent(kind: .wheelHere); e.wheelY = clamp(i16(m, 1)); e.wheelX = clamp(i16(m, 3)); return e
        default:
            return nil
        }
    }

    static func u16(_ m: [UInt8], _ o: Int) -> Int { return Int(m[o]) << 8 | Int(m[o + 1]) }
    static func i16(_ m: [UInt8], _ o: Int) -> Int { return Int(Int16(bitPattern: UInt16(m[o]) << 8 | UInt16(m[o + 1]))) }
    static func clamp(_ v: Int) -> Int { return max(-1200, min(1200, v)) }   // at most 10 notches
    static func step(_ v: Int) -> Int { return max(-2000, min(2000, v)) }    // at most 2000 pixels

    /// A point on the shown screen (0..65535 across it) in global display points.
    public static func toScreen(_ x: Int, _ y: Int, left: Double, top: Double, width: Double, height: Double) -> (Double, Double) {
        let px = left + Double(x) / 65535.0 * max(0, width - 1)
        let py = top + Double(y) / 65535.0 * max(0, height - 1)
        return (px, py)
    }
}

extension String {
    /// nil for broken UTF-8 (String(decoding:) would silently put U+FFFD in its place).
    init?(validatingUTF8Bytes b: [UInt8]) {
        var it = b.makeIterator()
        var dec = UTF8()
        var s = ""
        loop: while true {
            switch dec.decode(&it) {
            case .scalarValue(let u): s.unicodeScalars.append(u)
            case .emptyInput: break loop
            case .error: return nil
            }
        }
        self = s
    }
}
