// The encrypted link to a receiver (TV or phone), the same as cast/common/.../SecureLink.java and
// cast/pc/SecureLink.cs (see the description in the Java file), with CryptoKit:
//
//   HELLO     type | version | casterNonce 16 | casterEphemeral 65 | casterStatic 65 | UTF-8 name
//   HELLO_OK  type | version | receiverNonce 16 | receiverEphemeral 65 | receiverStatic 65
//             | casterNonce 16 (echo) | knowsYou byte | UTF-8 name
//   SEALED    type | counter 8 | AES-128-GCM(inner packet) | tag 16
//
// ee = SHA-256(ECDH x of the ephemerals), ss = the same of the statics; salt = SHA-256("CastLink v1"
// | nonces | ephemerals | statics); prk = HMAC(salt, ee | ss); HKDF-expand "keys" (40 bytes) and
// "code" (4 bytes -> the 6-digit pairing code). Golden values from tests/cs keep the three equal.

import CryptoKit
import Foundation

public enum SecureLink {
    public static let hello: UInt8 = 20, helloOk: UInt8 = 21, sealed: UInt8 = 22, pairShow: UInt8 = 24
    public static let version: UInt8 = 1
    public static let nonceBytes = 16, point = 65, tag = 16, overhead = 1 + 8 + 16
    public static let configPaired: UInt8 = 2
    public static let helloFixed = 2 + 16 + 65 + 65
    public static let helloOkFixed = 2 + 16 + 65 + 65 + 16 + 1

    public typealias PrivateKey = P256.KeyAgreement.PrivateKey

    /// 04 | X | Y
    public static func pointOf(_ k: PrivateKey) -> [UInt8] { return Array(k.publicKey.x963Representation) }

    /// a point on P-256 (CryptoKit refuses points off the curve: the "invalid curve" attack)
    public static func onCurve(_ p: [UInt8]) -> Bool {
        guard p.count == point, p[0] == 4 else { return false }
        return (try? P256.KeyAgreement.PublicKey(x963Representation: p)) != nil
    }

    /// SHA-256 of the ECDH shared x coordinate (what CNG's KDF Hash / SHA-256 gives)
    public static func agree(_ mine: PrivateKey, _ theirs: [UInt8]) throws -> [UInt8] {
        guard theirs.count == point, theirs[0] == 4 else { throw CryptoKitError.incorrectParameterSize }
        let pub = try P256.KeyAgreement.PublicKey(x963Representation: theirs)
        let shared = try mine.sharedSecretFromKeyAgreement(with: pub)
        let x = shared.withUnsafeBytes { Data($0) }
        return Array(SHA256.hash(data: x))
    }

    public static func randomNonce() -> [UInt8] {
        var g = SystemRandomNumberGenerator()
        return (0..<nonceBytes).map { _ in UInt8.random(in: 0...255, using: &g) }
    }

    public static func randomBytes(_ n: Int) -> [UInt8] {
        var g = SystemRandomNumberGenerator()
        return (0..<n).map { _ in UInt8.random(in: 0...255, using: &g) }
    }

    // ---- handshake messages -----------------------------------------------------------------

    public struct Hello {
        public var nonce: [UInt8], ephemeral: [UInt8], staticKey: [UInt8]
        public var echo: [UInt8]?
        public var knowsYou = false
        public var name = ""
    }

    static func utf8(_ s: String) -> [UInt8] { return Array(Array(s.utf8).prefix(64)) }

    public static func makeHello(nonce: [UInt8], ephemeral: [UInt8], staticKey: [UInt8], name: String) -> [UInt8] {
        return [hello, version] + nonce + ephemeral + staticKey + utf8(name)
    }

    /// a HELLO or HELLO_OK of this version, else nil
    public static func parse(_ b: [UInt8]) -> Hello? {
        let n = b.count
        guard n >= 2, b[1] == version, b[0] == hello || b[0] == helloOk else { return nil }
        let ok = b[0] == helloOk
        let fixed = ok ? helloOkFixed : helloFixed
        guard n >= fixed, n <= fixed + 64 else { return nil }
        var h = Hello(nonce: Array(b[2..<18]), ephemeral: Array(b[18..<83]), staticKey: Array(b[83..<148]))
        if ok {
            h.echo = Array(b[148..<164])
            h.knowsYou = b[fixed - 1] == 1
        }
        h.name = String(decoding: b[fixed..<n], as: UTF8.self)
        return h
    }

    public static func same(_ a: [UInt8]?, _ b: [UInt8]?) -> Bool {
        guard let a = a, let b = b, a.count == b.count else { return false }
        var d: UInt8 = 0
        for i in 0..<a.count { d |= a[i] ^ b[i] }
        return d == 0
    }

    // ---- the session --------------------------------------------------------------------------

    public static func derive(caster: Bool, myStatic: PrivateKey, myEphemeral: PrivateKey,
                              casterNonce: [UInt8], receiverNonce: [UInt8], casterEph: [UInt8], receiverEph: [UInt8],
                              casterStatic: [UInt8], receiverStatic: [UInt8]) throws -> Session {
        let ee = try agree(myEphemeral, caster ? receiverEph : casterEph)
        let ss = try agree(myStatic, caster ? receiverStatic : casterStatic)
        let label = Array("CastLink v1".utf8)
        let salt = Array(SHA256.hash(data: label + casterNonce + receiverNonce + casterEph + receiverEph + casterStatic + receiverStatic))
        let prk = Array(HMAC<SHA256>.authenticationCode(for: ee + ss, using: SymmetricKey(data: salt)))
        let keys = expand(prk, "keys", 40), code = expand(prk, "code", 4)
        let c = UInt32(code[0]) << 24 | UInt32(code[1]) << 16 | UInt32(code[2]) << 8 | UInt32(code[3])
        let pairCode = Int(c % 1_000_000)
        let c2r = Array(keys[0..<16]), r2c = Array(keys[16..<32]), cs = Array(keys[32..<36]), rs = Array(keys[36..<40])
        return caster ? Session(sendKey: c2r, sendSalt: cs, recvKey: r2c, recvSalt: rs, code: pairCode)
                      : Session(sendKey: r2c, sendSalt: rs, recvKey: c2r, recvSalt: cs, code: pairCode)
    }

    /// HKDF-Expand (RFC 5869), an ASCII label as info
    static func expand(_ prk: [UInt8], _ info: String, _ len: Int) -> [UInt8] {
        var out: [UInt8] = [], t: [UInt8] = []
        let key = SymmetricKey(data: prk)
        var i: UInt8 = 1
        while out.count < len {
            t = Array(HMAC<SHA256>.authenticationCode(for: t + Array(info.utf8) + [i], using: key))
            out += t
            i += 1
        }
        return Array(out.prefix(len))
    }

    public final class Session {
        let sendKey: SymmetricKey, recvKey: SymmetricKey
        let sendSalt: [UInt8], recvSalt: [UInt8]
        public let code: Int
        private var sendCounter: Int64 = -1
        private var highest: Int64 = -1
        private var window: UInt64 = 0
        private let sendLock = NSLock(), recvLock = NSLock()

        init(sendKey: [UInt8], sendSalt: [UInt8], recvKey: [UInt8], recvSalt: [UInt8], code: Int) {
            self.sendKey = SymmetricKey(data: sendKey)
            self.recvKey = SymmetricKey(data: recvKey)
            self.sendSalt = sendSalt
            self.recvSalt = recvSalt
            self.code = code
        }

        /// "123 456"
        public var codeText: String {
            let s = String(format: "%06ld", code)
            return String(s.prefix(3)) + " " + String(s.suffix(3))
        }

        /// one packet sealed (len + overhead bytes). Safe from several threads.
        public func seal(_ inner: ArraySlice<UInt8>) -> [UInt8] {
            sendLock.lock()
            sendCounter += 1
            let ctr = sendCounter
            sendLock.unlock()
            var head: [UInt8] = [SecureLink.sealed]
            Proto.putLong(&head, ctr)
            let nonce = try! AES.GCM.Nonce(data: sendSalt + SecureLink.be64(ctr))
            let box = try! AES.GCM.seal(inner, using: sendKey, nonce: nonce, authenticating: head)
            return head + Array(box.ciphertext) + Array(box.tag)
        }

        public func seal(_ inner: [UInt8]) -> [UInt8] { return seal(inner[...]) }

        /// the inner packet, or nil if not ours, changed or replayed
        public func open(_ b: [UInt8]) -> [UInt8]? {
            let n = b.count
            guard n >= SecureLink.overhead, b[0] == SecureLink.sealed else { return nil }
            let ctr = Proto.getLong(b, 1)
            recvLock.lock(); defer { recvLock.unlock() }
            if ctr < 0 || seen(ctr) { return nil }
            let len = n - SecureLink.overhead
            guard let nonce = try? AES.GCM.Nonce(data: recvSalt + SecureLink.be64(ctr)),
                  let box = try? AES.GCM.SealedBox(nonce: nonce, ciphertext: b[9..<(9 + len)], tag: b[(9 + len)..<n]),
                  let plain = try? AES.GCM.open(box, using: recvKey, authenticating: b[0..<9]) else { return nil }
            mark(ctr)
            return Array(plain)
        }

        private func seen(_ ctr: Int64) -> Bool {
            if highest < 0 || ctr > highest { return false }
            let back = highest - ctr
            return back >= 64 || window & (UInt64(1) << UInt64(back)) != 0
        }

        private func mark(_ ctr: Int64) {
            if ctr > highest {
                let shift = highest < 0 ? 64 : ctr - highest
                window = shift >= 64 ? 1 : (window << UInt64(shift)) | 1
                highest = ctr
            } else {
                window |= UInt64(1) << UInt64(highest - ctr)
            }
        }
    }

    static func be64(_ v: Int64) -> [UInt8] {
        var b: [UInt8] = []
        Proto.putLong(&b, v)
        return b
    }
}
