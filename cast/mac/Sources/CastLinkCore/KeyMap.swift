// Windows virtual-key codes (KEYRAW from a PC receiver) to macOS key codes. macOS key codes name
// places on the keyboard (ANSI layout); a Windows key code names the key's meaning, which on the
// usual layouts sits in the same place, so letters, digits and punctuation land right. Win is
// Command, Ctrl is Control, Alt is Option.

public enum KeyMap {
    public enum Modifier { case shift, control, option, command }

    /// the macOS key code for a Windows virtual-key code, nil for keys the Mac has no match for
    public static func macKey(vk: UInt8, extended: Bool) -> UInt16? {
        switch vk {
        case 0x0D: return extended ? 0x4C : 0x24          // Enter (numeric keypad Enter is extended)
        case 0x10: return 0x38                            // Shift
        case 0x11: return extended ? 0x3E : 0x3B          // Ctrl (right Ctrl is extended)
        case 0x12: return extended ? 0x3D : 0x3A          // Alt (right Alt / AltGr is extended)
        default: return table[vk]
        }
    }

    /// which modifier a macOS key code is, nil for the other keys
    public static func modifier(_ mac: UInt16) -> Modifier? {
        switch mac {
        case 0x38, 0x3C: return .shift
        case 0x3B, 0x3E: return .control
        case 0x3A, 0x3D: return .option
        case 0x37, 0x36: return .command
        default: return nil
        }
    }

    /// arrows, Home / End / Page Up / Page Down, Forward Delete, Help and F-keys carry the "Fn" flag
    /// on a Mac keyboard (arrows also "numeric pad"); some apps look at it
    public static func isFunctionKey(_ mac: UInt16) -> Bool {
        return [0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F, 0x69, 0x6B, 0x71, 0x6A,
                0x73, 0x77, 0x74, 0x79, 0x75, 0x72, 0x7B, 0x7C, 0x7D, 0x7E].contains(mac)
    }

    public static func isArrow(_ mac: UInt16) -> Bool { return mac >= 0x7B && mac <= 0x7E }

    static let table: [UInt8: UInt16] = {
        var t: [UInt8: UInt16] = [
            0x08: 0x33, 0x09: 0x30, 0x0C: 0x47, 0x14: 0x39, 0x1B: 0x35, 0x20: 0x31,   // Backspace Tab Clear Caps Esc Space
            0x21: 0x74, 0x22: 0x79, 0x23: 0x77, 0x24: 0x73,                            // PgUp PgDn End Home
            0x25: 0x7B, 0x26: 0x7E, 0x27: 0x7C, 0x28: 0x7D,                            // arrows
            0x2D: 0x72, 0x2E: 0x75,                                                    // Insert -> Help, Delete -> Forward Delete
            0x5B: 0x37, 0x5C: 0x36,                                                    // Win -> Command
            0x6A: 0x43, 0x6B: 0x45, 0x6D: 0x4E, 0x6E: 0x41, 0x6F: 0x4B,                // keypad * + - . /
            0xA0: 0x38, 0xA1: 0x3C, 0xA2: 0x3B, 0xA3: 0x3E, 0xA4: 0x3A, 0xA5: 0x3D,    // left / right Shift Ctrl Alt
            0xBA: 0x29, 0xBB: 0x18, 0xBC: 0x2B, 0xBD: 0x1B, 0xBE: 0x2F, 0xBF: 0x2C,    // ; = , - . /
            0xC0: 0x32, 0xDB: 0x21, 0xDC: 0x2A, 0xDD: 0x1E, 0xDE: 0x27, 0xE2: 0x0A,    // ` [ \ ] ' and the ISO key
        ]
        let letters: [UInt16] = [0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E,
                                 0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06]
        for (i, k) in letters.enumerated() { t[UInt8(0x41 + i)] = k }                    // A..Z
        let digits: [UInt16] = [0x1D, 0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19]
        for (i, k) in digits.enumerated() { t[UInt8(0x30 + i)] = k }                     // 0..9
        let keypad: [UInt16] = [0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5B, 0x5C]
        for (i, k) in keypad.enumerated() { t[UInt8(0x60 + i)] = k }                     // keypad 0..9
        let fkeys: [UInt16] = [0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F,
                               0x69, 0x6B, 0x71, 0x6A]
        for (i, k) in fkeys.enumerated() { t[UInt8(0x70 + i)] = k }                      // F1..F16
        return t
    }()
}
